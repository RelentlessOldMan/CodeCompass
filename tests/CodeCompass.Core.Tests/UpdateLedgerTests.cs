using System.IO;
using System.Linq;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// `update` rebuilt the whole ledger in RAM (a dictionary of every file, a set of every walked path) and rewrote the whole
// base even when nothing changed. It now edits the ledger in place and writes only the changes.
public sealed class UpdateLedgerTests
{
    [Fact]
    public void ANoOpUpdate_WritesNoNewBase_ButStillMovesTheLedgersWriteTime()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 20; i++) repo.Write($"src/f{i}.cs", $"class C{i} {{ }}");
        Reindex(repo);
        var dir = IndexStore.GetCacheDir(repo.Root);
        var basesBefore = Bases(dir);
        long recordedBefore;
        using (var s = DiskSnapshot.Open(dir)) recordedBefore = s.RecordedByUtcTicks;
        Thread.Sleep(50);

        var (t, sy, stats) = RepositoryIndexer.Update(repo.Root);
        using (t) using (sy) Assert.Equal((0, 0, 0), (stats.Added, stats.Modified, stats.Removed));

        Assert.Equal(basesBefore, Bases(dir)); // no full rewrite
        using var after = DiskSnapshot.Open(dir);
        Assert.Equal(20, after.Count);
        // LedgerTrust's bound moves on each update, as the full rewrite moved it.
        Assert.True(after.RecordedByUtcTicks > recordedBefore);
    }

    [Fact]
    public void AnUpdate_LeavesTheLedgerExactlyTheFilesOnDisk()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 12; i++) repo.Write($"src/f{i}.cs", $"class C{i} {{ }}");
        repo.Write("old/gone.cs", "class Gone { }");
        Reindex(repo);
        // A first update leaves some entries in the journal, so the next one sees an overlay over the base.
        repo.Write("src/f0.cs", "class C0 { int x; }");
        repo.Write("src/new1.cs", "class New1 { }");
        Update(repo);

        Thread.Sleep(50);
        repo.Write("src/f1.cs", "class C1 { int y; }");                     // modified (base row)
        repo.Write("src/f0.cs", "class C0 { int z; }");                     // modified again (overlay row)
        File.Delete(repo.FullPath("src/new1.cs"));                          // removed (overlay-only row)
        File.Delete(repo.FullPath("old/gone.cs"));                          // removed (base row)
        repo.Write("src/new2.cs", "class New2 { }");                        // added
        File.WriteAllBytes(repo.FullPath("src/f2.cs"), new byte[] { 0, 1, 2, 0, 0, 3 }); // became binary
        Update(repo);

        using var snap = DiskSnapshot.Open(IndexStore.GetCacheDir(repo.Root));
        var onDisk = Directory.GetFiles(repo.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".codecompass"))
            .Select(f => Path.GetRelativePath(repo.Root, f).Replace('\\', '/'))
            .Where(r => !r.StartsWith(".", StringComparison.Ordinal))
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.Equal(onDisk, snap.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        foreach (var rel in onDisk)
        {
            Assert.True(snap.TryGetValue(rel, out var st));
            var expected = rel == "src/f2.cs" ? FileState.BinaryHash : ContentHasher.Hash(File.ReadAllBytes(repo.FullPath(rel)));
            Assert.Equal(expected, st.ContentHash);
        }

        var (idx, sym, _) = RepositoryIndexer.Update(repo.Root);
        using var _s = sym;
        using var _i = idx;
        Assert.Empty(idx.Search("Gone"));
        Assert.Empty(idx.Search("New1"));
        Assert.NotEmpty(idx.Search("New2"));
        Assert.NotEmpty(idx.Search("int z"));
    }

    private static void Reindex(TempRepo repo)
    {
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
    }

    private static void Update(TempRepo repo)
    {
        var (t, s, _) = RepositoryIndexer.Update(repo.Root);
        t.Dispose(); s.Dispose();
    }

    private static string[] Bases(string dir) =>
        Directory.GetFiles(dir, "snapshot-*.base").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
}
