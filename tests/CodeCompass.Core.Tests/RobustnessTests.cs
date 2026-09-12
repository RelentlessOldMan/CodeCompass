using System;
using System.IO;
using System.Linq;
using System.Threading;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Crash-consistency / corruption fuzzing of the on-disk cache. The contract is: a damaged or
// half-written cache must DEGRADE (TryLoad returns false, or a search returns nothing/garbage without
// crashing) and a rebuild must always recover a correct index. It must NEVER take down the process
// (an AccessViolation from an unchecked mmap offset would kill the whole test run - which is exactly
// the failure these tests would surface). We fuzz on local temp dirs, restoring between cases.
public class RobustnessTests
{
    private static void BuildRepo(TempRepo repo)
    {
        repo.Write("a.cs", "namespace N { class Alpha { void MethodOne(){} } }");
        repo.Write("b.cs", "namespace N { class Betamax { void MethodTwo(){} } }");
        repo.Write("c.py", "def gamma_func():\n    return 42\n");
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
    }

    private static bool SearchFinds(string root, string q)
    {
        if (!RepositoryIndexer.TryLoad(root, out var t, out var s)) return false;
        using (t) using (s) return t.Search(q).Any();
    }

    // Open + search a (possibly corrupt) cache. Any managed exception is an acceptable "degraded read";
    // we only require that it doesn't crash the process.
    private static void TryOpenAndSearch(string root)
    {
        try
        {
            if (RepositoryIndexer.TryLoad(root, out var t, out var s))
                using (t) using (s) { _ = t.Search("Method").ToList(); _ = s.FindByName("Alpha").ToList(); }
        }
        catch { /* degraded read of a damaged cache - acceptable, must not crash */ }
    }

    // Write a cache file, tolerating a transient lock from an mmap handle not yet finalized after a
    // prior open (a truncated/corrupt file can keep its mapping alive briefly past Dispose).
    private static void WriteResilient(string path, byte[] bytes)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.WriteAllBytes(path, bytes); return; }
            catch (IOException) when (attempt < 5)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public void CorruptedArtifacts_NeverCrash_AndRebuildRecovers()
    {
        using var repo = new TempRepo();
        BuildRepo(repo);
        var dir = IndexStore.GetCacheDir(repo.Root);
        var files = Directory.GetFiles(dir);
        Assert.NotEmpty(files);

        foreach (var f in files)
        {
            var original = File.ReadAllBytes(f);
            try
            {
                // Flip the header region (magic / version / section offsets / counts) plus a middle
                // byte - the bytes most likely to drive an unchecked read if validation is missing.
                var b = (byte[])original.Clone();
                for (int i = 0; i < b.Length && i < 64; i++) b[i] ^= 0xFF;
                if (b.Length > 2) b[b.Length / 2] ^= 0xFF;
                WriteResilient(f, b);

                TryOpenAndSearch(repo.Root);
            }
            finally { WriteResilient(f, original); } // clean slate for the next artifact
        }

        // Every corruption was restored; a fresh build must yield a correct index.
        BuildRepo(repo);
        Assert.True(SearchFinds(repo.Root, "MethodOne"), "rebuild after corruption must recover search");
    }

    [Fact]
    public void TruncatedArtifacts_NeverCrash_AndRebuildRecovers()
    {
        using var repo = new TempRepo();
        BuildRepo(repo);
        var dir = IndexStore.GetCacheDir(repo.Root);

        foreach (var f in Directory.GetFiles(dir))
        {
            var original = File.ReadAllBytes(f);
            try
            {
                foreach (var newLen in new[] { 0, original.Length / 2, Math.Max(0, original.Length - 1) })
                {
                    WriteResilient(f, original.Take(newLen).ToArray()); // simulate a torn/short write
                    TryOpenAndSearch(repo.Root);
                }
            }
            finally { WriteResilient(f, original); }
        }

        BuildRepo(repo);
        Assert.True(SearchFinds(repo.Root, "gamma_func"), "rebuild after truncation must recover search");
    }

    [Fact]
    public void OrphanAndTempFiles_AreIgnored()
    {
        using var repo = new TempRepo();
        BuildRepo(repo);
        var dir = IndexStore.GetCacheDir(repo.Root);

        // Stray files as if a prior process died mid-write (higher-numbered orphan segments not in the
        // manifest, a bogus sidecar, a leftover temp) - all must be ignored, not read.
        File.WriteAllBytes(Path.Combine(dir, "seg-99999999.ccseg"), new byte[] { 1, 2, 3, 4, 5 });
        File.WriteAllBytes(Path.Combine(dir, "sym-99999999.ccsym"), new byte[] { 9, 9, 9 });
        File.WriteAllBytes(Path.Combine(dir, "pos-deadbeefdeadbeefdeadbeefdeadbeef.bin"), new byte[] { 0, 0, 0 });
        File.WriteAllText(Path.Combine(dir, "leftover.tmp"), "garbage");

        Assert.True(SearchFinds(repo.Root, "MethodOne"), "orphan/temp files must not break a valid index");
        Assert.True(SearchFinds(repo.Root, "gamma_func"));
    }
}
