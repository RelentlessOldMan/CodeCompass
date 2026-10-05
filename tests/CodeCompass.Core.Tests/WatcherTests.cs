using System;
using System.IO;
using System.Linq;
using System.Threading;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

public class WatcherTests
{
    [Fact]
    public void Watcher_ReindexesOnFileChange()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Existing { } }");
        RepositoryIndexer.Build(repo.Root);

        SegmentedSymbolIndex? latest = null;
        using var reindexed = new ManualResetEventSlim(false);

        using var watcher = new RepositoryWatcher(repo.Root, batch =>
        {
            var (_, symbols, _) = batch.FullReconcile
                ? RepositoryIndexer.Update(repo.Root)
                : RepositoryIndexer.UpdatePaths(repo.Root, batch.ChangedFullPaths);
            latest = symbols;
            reindexed.Set();
        }, debounceMs: 200);
        watcher.Start();

        // Add a new file; the watcher should notice and incrementally reindex.
        repo.Write("b.cs", "namespace N { class FreshlyAdded { } }");

        Assert.True(reindexed.Wait(TimeSpan.FromSeconds(15)), "watcher did not fire a reindex in time");
        Assert.NotNull(latest);
        Assert.NotEmpty(latest!.FindByName("FreshlyAdded"));
    }

    [Fact]
    public void Watcher_DoesNotFireAfterDispose()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");

        int fires = 0;
        // A generous debounce: Dispose must land before it elapses, and a loaded box (a coverage run) can stall for
        // hundreds of ms - with seconds of slack the "dispose beats debounce" precondition can't flake.
        var watcher = new RepositoryWatcher(repo.Root, _ => Interlocked.Increment(ref fires), debounceMs: 1500);
        watcher.Start();

        repo.Write("b.cs", "namespace N { class B { } }"); // schedule a flush...
        watcher.Dispose();                                  // ...then dispose before the debounce elapses

        Thread.Sleep(2500); // well past the debounce window
        Assert.Equal(0, Volatile.Read(ref fires)); // the pending flush must not run after Dispose
    }

    // Review P1-5: a FATAL watcher error (share hiccup, invalidated handle) left it dead - no further events, ever,
    // while it still looked enabled - so the index silently drifted. It is now replaced, and the lost window is
    // covered by a full-reconcile request.
    [Fact]
    public void Watcher_RestartsAfterAFatalError_AndKeepsDeliveringEvents()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");
        var batches = new System.Collections.Concurrent.ConcurrentQueue<ChangeBatch>();
        using var got = new ManualResetEventSlim(false);
        using var watcher = new RepositoryWatcher(repo.Root, b => { batches.Enqueue(b); got.Set(); }, debounceMs: 200);
        watcher.Start();

        watcher.RaiseErrorForTest(new IOException("The specified network name is no longer available."));
        for (int i = 0; i < 100 && watcher.Restarts == 0; i++) Thread.Sleep(50);
        Assert.Equal(1, watcher.Restarts);
        Assert.True(got.Wait(TimeSpan.FromSeconds(15)));
        Assert.Contains(batches, b => b.FullReconcile); // the events lost while it was dead are reconciled

        got.Reset();
        repo.Write("after.cs", "namespace N { class AfterRestart { } }");
        Assert.True(got.Wait(TimeSpan.FromSeconds(15)), "the replacement watcher delivered nothing");
        Assert.Contains(batches, b => b.ChangedFullPaths.Any(p => p.EndsWith("after.cs", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Watcher_BufferOverflow_ReconcilesWithoutRestarting()
    {
        using var repo = new TempRepo();
        using var got = new ManualResetEventSlim(false);
        ChangeBatch? last = null;
        using var watcher = new RepositoryWatcher(repo.Root, b => { last = b; got.Set(); }, debounceMs: 200);
        watcher.Start();
        watcher.RaiseErrorForTest(new InternalBufferOverflowException());
        Assert.True(got.Wait(TimeSpan.FromSeconds(15)));
        Assert.True(last!.FullReconcile);
        Assert.Equal(0, watcher.Restarts); // an overflowed watcher keeps running
    }

    // Review P2-13: the pending set was case-INsensitive, so a case-only rename delivered only the old spelling and the
    // index kept the stale casing.
    [Fact]
    public void Watcher_CaseOnlyRename_DeliversBothSpellings()
    {
        using var repo = new TempRepo();
        repo.Write("Widget.cs", "namespace N { class W { } }");
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var got = new ManualResetEventSlim(false);
        using var watcher = new RepositoryWatcher(repo.Root, b => { foreach (var p in b.ChangedFullPaths) seen.Add(Path.GetFileName(p)); got.Set(); }, debounceMs: 300);
        watcher.Start();
        File.Move(repo.FullPath("Widget.cs"), repo.FullPath("widget.cs"));
        Assert.True(got.Wait(TimeSpan.FromSeconds(15)));
        Thread.Sleep(500); // let a split batch land
        Assert.Contains("Widget.cs", seen);
        Assert.Contains("widget.cs", seen);
    }

    [Fact]
    public void Watcher_IgnoresChangesUnderIgnoredDirectories()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");
        RepositoryIndexer.Build(repo.Root);

        using var fired = new ManualResetEventSlim(false);
        using var watcher = new RepositoryWatcher(repo.Root, _ => fired.Set(), debounceMs: 200);
        watcher.Start();

        // Writing into an ignored directory (bin/) must NOT trigger a reindex.
        repo.Write("bin/generated.cs", "namespace N { class ShouldBeIgnored { } }");

        Assert.False(fired.Wait(TimeSpan.FromSeconds(3)), "watcher reacted to an ignored directory");
    }
}
