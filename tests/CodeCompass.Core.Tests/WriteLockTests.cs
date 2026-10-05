using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// Cross-process write safety (review P0-1). Before: only LINKED roots had any write guard, so two sessions on one
// repo (or a session + a terminal `codecompass index`) were unmediated writers to one cache dir - colliding segment
// numbers, last-writer-wins manifests, each side's orphan cleanup deleting the other's live segments. Now every
// mutation holds IndexWriteLock, and a process holding the index in memory reloads when another commits.
public class WriteLockTests
{
    private static string TempCacheDir() =>
        Path.Combine(Path.GetTempPath(), "cc-lock-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteLock_ExcludesOtherThreads_NestsOnSameThread()
    {
        var dir = TempCacheDir();
        using var a = IndexWriteLock.TryAcquire(dir);
        Assert.NotNull(a);
        using (var nested = IndexWriteLock.TryAcquire(dir)) Assert.NotNull(nested); // Update -> Build fallback nests

        IndexWriteLock? other = null;
        var t = new Thread(() => other = IndexWriteLock.TryAcquire(dir));
        t.Start(); t.Join();
        Assert.Null(other); // a different thread (like a different process) is excluded while held
    }

    [Fact]
    public void WriteLock_ReleaseBumpsGeneration_CommitReportsIt_SkipBumpLeavesItAlone()
    {
        var dir = TempCacheDir();
        Assert.Equal("", IndexGeneration.Read(dir));

        using (IndexWriteLock.TryAcquire(dir)) { }
        var g1 = IndexGeneration.Read(dir);
        Assert.False(string.IsNullOrEmpty(g1)); // a plain release publishes a new generation

        string committed;
        using (var lk = IndexWriteLock.TryAcquire(dir)!) committed = lk.Commit();
        Assert.Equal(committed, IndexGeneration.Read(dir)); // Commit's token is exactly what readers will see
        Assert.NotEqual(g1, committed);

        using (var lk = IndexWriteLock.TryAcquire(dir)!) lk.SkipBump();
        Assert.Equal(committed, IndexGeneration.Read(dir)); // a no-op hold doesn't make other processes reload
    }

    [Fact]
    public void WriteLock_Acquire_WaitsForTheHolder_AndIsCancellable()
    {
        var dir = TempCacheDir();
        var holder = IndexWriteLock.TryAcquire(dir)!;
        using var acquired = new ManualResetEventSlim(false);
        var waiter = Task.Run(() => { using (IndexWriteLock.Acquire(dir)) acquired.Set(); });

        Assert.False(acquired.Wait(300)); // must NOT get in while the holder writes
        holder.Dispose();
        Assert.True(waiter.Wait(10_000)); // ...and proceeds once it releases
        Assert.True(acquired.IsSet);

        using var stillHeld = IndexWriteLock.TryAcquire(dir)!;
        using var cts = new CancellationTokenSource();
        var cancelled = Task.Run(() => IndexWriteLock.Acquire(dir, cts.Token));
        cts.Cancel(); // a re-point/shutdown abandons the wait instead of blocking on a long external build
        var ex = Assert.ThrowsAny<AggregateException>(() => cancelled.Wait(10_000));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    // Freshness: a session holding the index must serve what ANOTHER writer committed, not a silently stale copy.
    [Fact]
    public void Server_ReloadsWhenAnotherWriterCommits()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.SearchCode("AlphaLocal"));

            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            // Another writer (a terminal `codecompass update`) - on its own thread, so it's a distinct lock holder.
            Task.Run(() => { var u = RepositoryIndexer.Update(repo.Root); u.Text.Dispose(); u.Symbols.Dispose(); }).Wait();

            Assert.Contains("b.cs", CodeCompassTools.SearchCode("BravoExternal"));
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // The corruption case: a live-watching session applying an edit must not flush its STALE manifest over another
    // writer's newer one (which dropped that writer's segment from the index on disk).
    [Fact]
    public void Server_IncrementalAfterExternalWrite_KeepsTheOtherWritersSegments()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            Task.Run(() => { var u = RepositoryIndexer.Update(repo.Root); u.Text.Dispose(); u.Symbols.Dispose(); }).Wait();

            repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));

            // What's ON DISK (a fresh process's view) must hold both writers' work.
            Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
            using (t) using (s)
            {
                Assert.NotEmpty(t.Search("BravoExternal"));
                Assert.NotEmpty(t.Search("CharlieLocal"));
            }
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // One live watcher per repo: a second session serves read-only (no watcher) and takes over when the first exits.
    [Fact]
    public void Server_SecondSession_ServesReadOnly_ThenTakesOverLiveIndexing()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class RoleProbe { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();

        var otherSession = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(repo.Root));
        Assert.NotNull(otherSession);
        ServerContext.Init(repo.Root);
        try
        {
            Assert.False(ServerContext.IsLiveWatchOwnerForTest);
            ServerContext.EnableLiveIndex();
            Assert.False(ServerContext.HasWatcherForTest);                      // the owner watches, not us
            Assert.Contains("a.cs", CodeCompassTools.SearchCode("RoleProbe")); // ...but we still serve

            otherSession!.Dispose();                                           // the owning session exits
            CodeCompassTools.SearchCode("RoleProbe");                          // next query takes over the role
            Assert.True(ServerContext.IsLiveWatchOwnerForTest);
            Assert.True(ServerContext.HasWatcherForTest);
        }
        finally
        {
            otherSession?.Dispose();
            ServerContext.Init(repo.Root);
        }
    }

    // Review P1-6: a watcher overflow on a network/huge root used to start an UNGATED in-session full rebuild (hours
    // over SMB). Now it keeps serving, discloses possible staleness on every query, and clears once a fresh update
    // (here another process's) is installed.
    [Fact]
    public void Server_LostEventsOnNetworkRoot_DisclosesInsteadOfRebuilding_ClearsAfterUpdate()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class LostEventsProbe { } }");
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            ServerContext.OnChangesForTest(new ChangeBatch(Array.Empty<string>(), FullReconcile: true));

            var r = CodeCompassTools.SearchCode("LostEventsProbe");
            Assert.Contains("a.cs", r);                    // still serving (no rebuild took it down)
            Assert.Contains("lost change events", r);      // ...and says results may be stale
            Assert.Contains("codecompass update", r);

            Thread.Sleep(20); // meta timestamps are compared against the loss time
            Task.Run(() => { var u = RepositoryIndexer.Update(repo.Root); u.Text.Dispose(); u.Symbols.Dispose(); }).Wait();
            Assert.DoesNotContain("lost change events", CodeCompassTools.SearchCode("LostEventsProbe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", old);
            ServerContext.Init(repo.Root);
        }
    }

    // Review P2-30: the live-watch role across REAL processes - a running `codecompass watch` holds it (so a session
    // here can't start a second watcher), and killing that process releases it with no stale lock left behind.
    [Fact]
    public void LiveWatchRole_HeldByAnotherProcess_ThenFreedWhenItIsKilled()
    {
        var cli = TestCli.Find();
        if (cli is null) return;

        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class WatchRole { } }");
        var cacheDir = IndexStore.GetCacheDir(repo.Root);
        using var p = TestCli.Start(cli, "watch", repo.Root);
        try
        {
            var watching = Task.Run(() =>
            {
                string? line;
                while ((line = p.StandardError.ReadLine()) is not null)
                    if (line.Contains("watching")) return true;
                return false;
            });
            Assert.True(watching.Wait(120_000) && watching.Result, "the CLI watch never started");
            _ = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();

            Assert.Null(WriteOwnership.TryAcquire(cacheDir)); // held by the live `watch` process
        }
        finally
        {
            p.Kill(entireProcessTree: true); // a crash/kill, not a clean exit
            p.WaitForExit(30_000);
        }

        WriteOwnership? mine = null;
        for (int i = 0; i < 100 && mine is null; i++) { mine = WriteOwnership.TryAcquire(cacheDir); if (mine is null) Thread.Sleep(100); }
        Assert.NotNull(mine); // the OS dropped the dead process's handle - nothing to clean up by hand
        mine!.Dispose();
    }

    // Real cross-process exclusion: a terminal `codecompass index` waits for an in-flight write held by another
    // process (this test host), says so, and completes once it's released - it is never refused.
    [Fact]
    public void Cli_Index_WaitsForAnotherProcessesWrite_ThenCompletes()
    {
        var cli = TestCli.Find();
        if (cli is null) return;

        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class CrossProcess { } }");
        var holder = IndexWriteLock.TryAcquire(IndexStore.GetCacheDir(repo.Root))!;
        using var p = TestCli.Start(cli, "index", repo.Root);
        // stderr carries \r-rewritten progress lines first; read until the wait notice (or the CLI exits).
        var sawNotice = Task.Run(() =>
        {
            string? line;
            while ((line = p.StandardError.ReadLine()) is not null)
                if (line.Contains("waiting for another CodeCompass process")) return true;
            return false;
        });
        try
        {
            Assert.True(sawNotice.Wait(60_000) && sawNotice.Result, "the CLI never reported waiting for the lock");
            Assert.False(p.HasExited); // blocked, not failed
        }
        finally { holder.Dispose(); }

        _ = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000));
        Assert.Equal(0, p.ExitCode);
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
        using (t) using (s) Assert.NotEmpty(t.Search("CrossProcess"));
    }
}
