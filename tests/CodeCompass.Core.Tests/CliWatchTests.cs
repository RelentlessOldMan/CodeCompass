using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// `codecompass watch` when another process's write can't be reloaded: held-back edits were retried only on the next file
// change (there may be none), a missing or corrupt index held every edit forever, and a newer CodeCompass's index crashed
// it with an unhandled exception while it held the live-watch role.
public class CliWatchTests
{
    private static void OtherWriter(string root)
    {
        var u = RepositoryIndexer.Update(root);
        u.Text.Dispose(); u.Symbols.Dispose();
    }

    private static bool IndexHas(string root, string word)
    {
        if (!RepositoryIndexer.TryLoad(root, out var t, out var s)) return false;
        using (t) using (s) return t.Search(word).Count > 0;
    }

    private static async Task<ConcurrentQueue<string>> StartedWatch(System.Diagnostics.Process p)
    {
        var lines = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            string? line;
            while ((line = p.StandardError.ReadLine()) is not null)
            {
                lines.Enqueue(line);
                if (line.Contains("watching")) started.TrySetResult(true);
            }
            started.TrySetResult(false);
        });
        _ = p.StandardOutput.ReadToEndAsync();
        Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(120)), "the CLI watch never started");
        return lines;
    }

    [Fact]
    public void Watch_ANewerFormatIndex_SaysSo_InsteadOfCrashing()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        Assert.Equal(0, TestCli.Run(cli, "index", repo.Root).Exit);
        foreach (var seg in Directory.GetFiles(IndexStore.CacheDirPath(repo.Root), "seg-*.ccseg"))
            using (var fs = new FileStream(seg, FileMode.Open, FileAccess.Write)) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }

        var (exit, _, stderr) = TestCli.Run(cli, "watch", repo.Root);
        Assert.Equal(1, exit);
        Assert.Contains("newer CodeCompass", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    [Fact]
    public async Task Watch_AHeldBackEdit_IsRetried_WithoutAnotherChange()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        using var p = TestCli.Start(cli, "watch", repo.Root);
        try
        {
            var lines = await StartedWatch(p);
            var cache = IndexStore.CacheDirPath(repo.Root);
            var symbolsManifest = Path.Combine(cache, "symbols.manifest");
            FileStream unreadable;
            using (var wl = IndexWriteLock.Acquire(cache)) // the watch's batch waits for this
            {
                OtherWriter(repo.Root); // another process's commit (this thread's hold is re-entrant)...
                repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
                Thread.Sleep(2000);     // the watch's batch for it is now waiting for the lock
                unreadable = new FileStream(symbolsManifest, FileMode.Open, FileAccess.Read, FileShare.None); // ...that it can't reload, just now
            }
            using (unreadable)
            {
                for (int i = 0; i < 100 && !lines.Any(l => l.Contains("holding back")); i++) Thread.Sleep(100);
                Assert.Contains(lines, l => l.Contains("holding back"));
            }

            bool landed = false; // no further edit: the retry alone must land it
            for (int i = 0; i < 150 && !landed; i++) { Thread.Sleep(100); landed = IndexHas(repo.Root, "CharlieLocal"); }
            Assert.True(landed, "the held-back edit never landed: " + string.Join(" | ", lines));
        }
        finally
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(30_000);
        }
    }

    [Fact]
    public async Task Watch_ACorruptIndex_IsRebuilt_NotHeldForever()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var cache = IndexStore.CacheDirPath(repo.Root);
        using var p = TestCli.Start(cli, "watch", repo.Root);
        try
        {
            var lines = await StartedWatch(p);
            using (var wl = IndexWriteLock.Acquire(cache)) // the watch's batch waits for this
            {
                var before = Directory.GetFiles(cache, "seg-*.ccseg");
                repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
                Thread.Sleep(2000);     // its batch is now waiting for the lock
                OtherWriter(repo.Root); // another process's commit (it indexes c.cs into a new segment)...
                foreach (var seg in Directory.GetFiles(cache, "seg-*.ccseg").Except(before))
                    using (var fs = new FileStream(seg, FileMode.Open, FileAccess.Write)) fs.Write(new byte[4]); // ...left corrupt
            }
            bool landed = false;
            for (int i = 0; i < 150 && !landed; i++) { Thread.Sleep(100); landed = IndexHas(repo.Root, "CharlieLocal"); }
            Assert.True(landed, "a corrupt index held the edit forever: " + string.Join(" | ", lines));
        }
        finally
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(30_000);
        }
    }

    // An ignore file's pattern taken out: `watch` only said to run `codecompass update`, even on a small local repo where the
    // MCP server reconciles by itself. It now reconciles where the server would (RepositoryIndexer.AutoReconciles).
    [Fact]
    public async Task Watch_AnIgnorePatternRemoved_OnASmallLocalRepo_IndexesTheFileAgain()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        repo.Write(".gitignore", "*.tmp\n");
        repo.Write("keep.tmp", "UnignoredWord");
        using var p = TestCli.Start(cli, "watch", repo.Root);
        try
        {
            var lines = await StartedWatch(p);
            Assert.False(IndexHas(repo.Root, "UnignoredWord"));
            repo.Write(".gitignore", "# nothing ignored now\n");
            bool landed = false;
            for (int i = 0; i < 300 && !landed; i++) { Thread.Sleep(100); landed = IndexHas(repo.Root, "UnignoredWord"); }
            Assert.True(landed, "the file no longer ignored was never indexed: " + string.Join(" | ", lines));
        }
        finally
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(30_000);
        }
    }

    // Where the server wouldn't reconcile (autoReconcile off here; a network share or a repo over the size limit alike), the
    // watch still says what to run and doesn't walk.
    [Fact]
    public async Task Watch_AnIgnorePatternRemoved_WithAutoReconcileOff_SaysToUpdate()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        repo.Write(".codecompass.json", "{ \"autoReconcile\": false }");
        repo.Write(".gitignore", "*.tmp\n");
        repo.Write("keep.tmp", "UnignoredWord");
        using var p = TestCli.Start(cli, "watch", repo.Root);
        try
        {
            var lines = await StartedWatch(p);
            repo.Write(".gitignore", "# nothing ignored now\n");
            for (int i = 0; i < 300 && !lines.Any(l => l.Contains("an ignore file changed")); i++) Thread.Sleep(100);
            Assert.Contains(lines, l => l.Contains("an ignore file changed") && l.Contains("codecompass update"));
            Assert.False(IndexHas(repo.Root, "UnignoredWord"));
        }
        finally
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(30_000);
        }
    }

    // At start, an index briefly unreadable (another process has it open - a terminal `index` committing, a virus scan)
    // was treated as missing: the watch rebuilt the whole repo over it.
    [Fact]
    public void Watch_AtStart_ABrieflyUnreadableIndex_IsNotRebuilt()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        Assert.Equal(0, TestCli.Run(cli, "index", repo.Root).Exit);
        var symbolsManifest = Path.Combine(IndexStore.CacheDirPath(repo.Root), "symbols.manifest");
        int exit; string stderr;
        using (new FileStream(symbolsManifest, FileMode.Open, FileAccess.Read, FileShare.None))
            (exit, _, stderr) = TestCli.Run(cli, "watch", repo.Root);
        Assert.Equal(1, exit);
        Assert.Contains("could not be read right now", stderr);
        Assert.DoesNotContain("building initial index", stderr);
    }

    // Stopped mid-watch by a newer CodeCompass's index, the exit note said to run `codecompass update` - which this version
    // refuses for that index.
    [Fact]
    public async Task Watch_StoppedByANewerFormatIndex_PointsAtWhatCanStillLandTheEdits()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var cache = IndexStore.CacheDirPath(repo.Root);
        using var p = TestCli.Start(cli, "watch", repo.Root);
        try
        {
            var lines = await StartedWatch(p);
            using (var wl = IndexWriteLock.Acquire(cache)) // the watch's batch waits for this
            {
                var before = Directory.GetFiles(cache, "seg-*.ccseg");
                repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
                Thread.Sleep(2000);     // its batch is now waiting for the lock
                OtherWriter(repo.Root); // a newer CodeCompass's commit
                foreach (var seg in Directory.GetFiles(cache, "seg-*.ccseg").Except(before))
                    using (var fs = new FileStream(seg, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }
            }
            Assert.True(p.WaitForExit(60_000), "the watch never stopped: " + string.Join(" | ", lines));
            Assert.Equal(1, p.ExitCode);
            for (int i = 0; i < 50 && !lines.Any(l => l.Contains("held back")); i++) Thread.Sleep(100);
            var note = lines.FirstOrDefault(l => l.Contains("held back")) ?? "";
            Assert.Contains("codecompass index", note);
            Assert.DoesNotContain("codecompass update", note);
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(30_000);
        }
    }
}
