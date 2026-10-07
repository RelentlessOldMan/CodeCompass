using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Field report (co-worker v2/v3): `cache clear` under a live server deleted file by file and stopped at the first one the
// server held open - a half-deleted index the server kept serving, and `clear-all` swallowed the error. Clearing a cache
// is all or nothing: refused (and left intact) while anything holds it open, removed whole otherwise.
public class CacheClearTests
{
    [Fact]
    public void TryClearCacheDir_InUse_IsRefused_AndLeftIntact_ThenClearsWhole()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.bin"), "a");
        File.WriteAllText(Path.Combine(dir, "sub", "b.bin"), "b");
        File.WriteAllText(Path.Combine(dir, "z.idx"), "z");
        try
        {
            // Held the way a running server holds its memory-mapped index (read, no delete sharing).
            using (new FileStream(Path.Combine(dir, "z.idx"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(IndexStore.TryClearCacheDir(dir, out var why));
                Assert.Contains("in use", why);
                Assert.True(File.Exists(Path.Combine(dir, "a.bin")));          // nothing was deleted
                Assert.True(File.Exists(Path.Combine(dir, "sub", "b.bin")));
                Assert.True(File.Exists(Path.Combine(dir, "z.idx")));
            }

            Assert.True(IndexStore.TryClearCacheDir(dir, out var error), error);
            Assert.False(Directory.Exists(dir));
            Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(dir)!, Path.GetFileName(dir) + "*")); // nothing left aside
        }
        finally
        {
            foreach (var d in Directory.EnumerateDirectories(Path.GetDirectoryName(dir)!, Path.GetFileName(dir) + "*"))
                try { Directory.Delete(d, true); } catch { }
        }
    }

    // A cache that is already gone (another clear or gc won the race) is cleared, not "in use".
    [Fact]
    public void TryClearCacheDir_AlreadyGone_IsCleared_NotInUse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-clear-gone-" + Guid.NewGuid().ToString("N"));
        Assert.True(IndexStore.TryClearCacheDir(dir, out var why), why);
        Assert.Equal("", why);
    }

    // `link remove` with purge deletes the linked root's index - which the MCP server typically still holds open. Same rule:
    // kept whole and reported, never half-deleted.
    [Fact]
    public void LinkRemove_PurgeOfIndexInUse_KeepsItWhole()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        lib.Write("lib.c", "int lib(void){ return 1; }\n");
        var (t, s, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(lib.Root); t.Dispose(); s.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        var cacheDir = IndexStore.CacheDirPath(lib.Root);
        var files = Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories).ToList();

        using (new FileStream(files[^1], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = LinkManager.Remove(project.Root, lib.Root, _ => true);
            Assert.Equal(LinkManager.RemoveStatus.PurgeFailed, r.Status);
            Assert.Contains("in use", r.Message);
            Assert.All(files, f => Assert.True(File.Exists(f), f));
        }
        IndexStore.TryClearCacheDir(cacheDir, out _); // released now: don't leave it in the test cache
    }

    // The CLI says so and fails, rather than reporting a partial clear as done.
    [Fact]
    public void Cli_CacheClear_InUse_RefusesAndExitsNonZero()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.c", "int a(void){ return 0; }\n");
        Assert.Equal(0, TestCli.Run(cli, "index", repo.Root).Exit);
        var cacheDir = IndexStore.CacheDirPath(repo.Root);
        var held = Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories).First();

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var (exit, stdout, stderr) = TestCli.Run(cli, "cache", "clear", repo.Root);
            Assert.NotEqual(0, exit);
            Assert.Contains("in use", stdout + stderr);
            Assert.True(File.Exists(held));
        }

        Assert.Equal(0, TestCli.Run(cli, "cache", "clear", repo.Root).Exit);
        Assert.False(Directory.Exists(cacheDir));
    }
}
