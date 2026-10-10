using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Config;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Index writes that must not happen, or must not cost what they did.
public class IndexGuardTests
{
    private static string CacheDir(TempRepo repo) => IndexStore.CacheDirPath(repo.Root);

    // Rebuilding over an index a NEWER CodeCompass wrote: two installs sharing a cache would rebuild each other's index
    // forever, and a session that can't read it would replace it with an older format. Only a deliberate `index` may.
    [Fact]
    public void Build_RefusesAnIndexFromANewerFormat_UnlessDeliberate()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class NewerProbe { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var seg = Directory.GetFiles(CacheDir(repo), "seg-*.ccseg").First();
        using (var fs = new FileStream(seg, FileMode.Open, FileAccess.Write)) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }
        var before = File.ReadAllBytes(seg);

        Assert.Throws<InvalidOperationException>(() => RepositoryIndexer.Build(repo.Root));
        Assert.Equal(before, File.ReadAllBytes(seg)); // not rebuilt over

        var (t, s, _) = RepositoryIndexer.Build(repo.Root, overNewerFormat: true); // `codecompass index`: deliberate
        using (t) using (s) Assert.NotEmpty(t.Search("NewerProbe"));
    }

    // A refused write wrote nothing - so it must publish no new generation. It did (the write lock's release bumps it), and a
    // session retrying a held-back edit every few seconds made every other session reload, and drop its C# model, each time.
    [Fact]
    public void ARefusedWrite_PublishesNoNewGeneration()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class RefusedProbe { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var seg = Directory.GetFiles(CacheDir(repo), "seg-*.ccseg").First();
        using (var fs = new FileStream(seg, FileMode.Open, FileAccess.Write)) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }
        var gen = IndexGeneration.Read(CacheDir(repo));

        Assert.Throws<InvalidOperationException>(() => RepositoryIndexer.UpdatePaths(repo.Root, new[] { repo.FullPath("a.cs") }));
        Assert.Throws<InvalidOperationException>(() => RepositoryIndexer.Update(repo.Root));
        Assert.Throws<InvalidOperationException>(() => RepositoryIndexer.Build(repo.Root));
        Assert.Equal(gen, IndexGeneration.Read(CacheDir(repo)));
    }

    // A folder in a batch whose walk misses files that are still there - a subfolder listing that comes back empty without
    // failing (no list permission; a share hiccup) - must not drop them from the index.
    [Fact]
    public void AFolderSweep_KeepsFilesItCouldntList_ButStillExist()
    {
        if (!OperatingSystem.IsWindows()) return; // the deny rule below is a Windows ACL
        using var repo = new TempRepo();
        repo.Write("src/sub/Kept.cs", "class SweepKeptProbe { }");
        repo.Write("src/a.cs", "class A { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();

        var sub = new DirectoryInfo(repo.FullPath("src/sub"));
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(user, System.Security.AccessControl.FileSystemRights.ListDirectory,
                                                                         System.Security.AccessControl.AccessControlType.Deny);
        var acl = sub.GetAccessControl();
        acl.AddAccessRule(deny);
        sub.SetAccessControl(acl);
        try
        {
            var (t, s, _) = RepositoryIndexer.UpdatePaths(repo.Root, new[] { repo.FullPath("src") });
            using (t) using (s) Assert.NotEmpty(t.Search("SweepKeptProbe"));
        }
        finally
        {
            acl = sub.GetAccessControl();
            acl.RemoveAccessRule(deny);
            sub.SetAccessControl(acl);
        }
    }

    // A C/C++ clean or rebuild deletes thousands of .obj/.pdb files; the watcher passes on paths that are gone (one may have
    // been a folder named like an ignored type), but the index never held them - they must cost nothing, and above all not
    // push a small batch past the full-rebuild threshold.
    [Fact]
    public void ABuildCleanOfThousandsOfObjectFiles_IsNotAFullRebuild()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class CleanProbe { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();

        var cleaned = Enumerable.Range(0, 2500).Select(i => repo.FullPath($"intermediate/f{i}.obj")).ToList();
        var (t, s, stats) = RepositoryIndexer.UpdatePaths(repo.Root, cleaned);
        t.Dispose(); s.Dispose();
        Assert.False(stats.FullRebuild);
    }

    // The auto-index size check for a linked root must skip what THAT root's config ignores - not whatever config the
    // calling thread carries (a server's background work carries the project's).
    [Fact]
    public void ExceedsAutoLimit_SizesARootWithItsOwnIgnoreRules()
    {
        using var lib = new TempRepo();
        using var other = new TempRepo();
        lib.Write(".codecompass.json", "{ \"ignore\": [\"generated\"], \"maxAutoMb\": 1 }");
        lib.Write("src/a.cs", "class Small { }");
        lib.WriteBytes("generated/big.cs", new byte[3 * 1024 * 1024]);
        try
        {
            CodeCompassConfig.Load(other.Root); // the caller's config: no ignore
            Assert.False(RepositoryIndexer.ExceedsAutoLimit(lib.Root, CodeCompassConfig.ReadFrom(lib.Root)!, out _));
        }
        finally { CodeCompassConfig.Load(other.Root); }
    }
}
