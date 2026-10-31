using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// Re-point epoch guard: a background build/reconcile that started for the PREVIOUS
// workspace must never install its result after Init() re-pointed the server - that would silently serve the
// wrong repository. Shares the compaction-env collection so it runs serially against the other tests that
// drive ServerContext's static state.
[Collection("compaction-env")]
public class RepointEpochTests
{
    [Fact]
    public void Swap_WithStaleEpoch_IsDiscarded_CurrentEpochInstalls()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");

        ServerContext.Init(repo.Root);
        int staleEpoch = ServerContext.EpochForTest;

        ServerContext.Init(repo.Root); // re-point -> bumps the epoch; a build begun at staleEpoch is now stale
        Assert.NotEqual(staleEpoch, ServerContext.EpochForTest);

        // A result carrying the stale epoch must be discarded (and its handles disposed), NOT installed.
        var stale = RepositoryIndexer.Build(repo.Root);
        Assert.False(ServerContext.SwapForTest(stale.Text, stale.Symbols, staleEpoch));
        Assert.False(ServerContext.IsServingForTest); // nothing installed from a stale build

        // A result carrying the current epoch installs normally.
        var fresh = RepositoryIndexer.Build(repo.Root);
        Assert.True(ServerContext.SwapForTest(fresh.Text, fresh.Symbols, ServerContext.EpochForTest));
        Assert.True(ServerContext.IsServingForTest);

        ServerContext.Init(repo.Root); // leave clean for the next test in the collection (disposes what we installed)
    }

    // The startup reconcile a load schedules read the CURRENT root when its task ran. Late (a busy thread pool), it ran
    // after a re-point and reconciled the NEW workspace under the new epoch - racing that session's own writes (the
    // WriteLockTests flake: it mapped a segment a test was about to patch, or held the build gate so an edit was only
    // captured). A task scheduled for one workspace must never act on another.
    [Fact]
    public void AStartupReconcile_ThatStartsAfterARepoint_DoesNothingToTheNewWorkspace()
    {
        using var a = new TempRepo();
        using var b = new TempRepo();
        a.Write("a.cs", "class InA { }");
        b.Write("b.cs", "class InB { }");
        foreach (var r in new[] { a, b }) { var x = RepositoryIndexer.Build(r.Root); x.Text.Dispose(); x.Symbols.Dispose(); }

        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var ended = new ManualResetEventSlim();
        int calls = 0;
        ServerContext.StartupReconcileHookForTest = start =>
        {
            if (Interlocked.CompareExchange(ref calls, 1, 0) != 0 && start) return; // only the first task is held
            if (start) { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); }
            else ended.Set();
        };
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "true");
            ServerContext.Init(a.Root);
            CodeCompassTools.SearchCode("InA"); // loads A's index, which schedules its startup reconcile
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the startup reconcile never started");

            ServerContext.Init(b.Root);         // re-point before that task gets going
            b.Write("late.cs", "class LateInB { }");
            release.Set();
            Assert.True(ended.Wait(TimeSpan.FromSeconds(60)), "the startup reconcile never finished");

            // B's index on disk is as B's own session left it: nobody indexed late.cs behind its back.
            Assert.True(RepositoryIndexer.TryLoad(b.Root, out var text, out var symbols));
            using (text) using (symbols) Assert.Empty(text.Search("LateInB"));
        }
        finally
        {
            ServerContext.StartupReconcileHookForTest = null;
            release.Set();
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(a.Root);
        }
    }
}
