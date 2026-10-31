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

    // A FATAL watcher error (share hiccup, invalidated handle) left it dead - no further events, ever,
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

    // The pending set was case-INsensitive, so a case-only rename delivered only the old spelling and the
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

    // Adding an entry to a folder moves the folder's own timestamp, and Windows reports that as a Changed event for the
    // folder. Passed on, it made the indexer re-walk the folder's whole subtree under the write lock - on every atomic save
    // in it (a temp file + rename) - and made the server think a C# source may have changed.
    [Fact]
    public void Watcher_DoesNotPassOnAFolderWhoseTimestampMoved()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "namespace N { class A { } }");
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var got = new ManualResetEventSlim(false);
        using var watcher = new RepositoryWatcher(repo.Root, b => { foreach (var p in b.ChangedFullPaths) seen.Add(p); got.Set(); }, debounceMs: 300);
        watcher.Start();

        repo.Write("src/notes.txt", "added");
        Assert.True(got.Wait(TimeSpan.FromSeconds(15)));
        Thread.Sleep(800); // let a split batch land
        Assert.Contains(seen, p => p.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(seen, p => string.Equals(p.TrimEnd('\\', '/'), repo.FullPath("src"), StringComparison.OrdinalIgnoreCase));
    }

    // A folder named like an ignored file type (Contoso.Lib - .lib) is still a folder. Moved out of the repo, its own event is
    // the only one its files get, and it was dropped: the index (and the C# analyzer) kept files that no longer exist.
    [Fact]
    public void Watcher_PassesOnAFolderNamedLikeAnIgnoredFileType()
    {
        using var repo = new TempRepo();
        using var outside = new TempRepo();
        repo.Write("Contoso.Lib/a.cs", "namespace N { class A { } }");
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var got = new ManualResetEventSlim(false);
        using var watcher = new RepositoryWatcher(repo.Root, b => { foreach (var p in b.ChangedFullPaths) seen.Add(p); got.Set(); }, debounceMs: 300);
        watcher.Start();

        Directory.Move(repo.FullPath("Contoso.Lib"), Path.Combine(outside.Root, "Contoso.Lib"));
        Assert.True(got.Wait(TimeSpan.FromSeconds(15)), "moving the folder out produced no change");
        Assert.Contains(seen, p => p.EndsWith("Contoso.Lib", StringComparison.OrdinalIgnoreCase));
    }

    // ...and renamed TO such a name (src/Lib -> src/Contoso.Lib): the old side went through and removed the folder's files,
    // the new side was dropped by name, so the whole folder vanished from the index until a full update.
    [Fact]
    public void Watcher_AndIndexer_AFolderRenamedToAnIgnoredTypesName_KeepsItsFiles()
    {
        using var repo = new TempRepo();
        repo.Write("src/Lib/widget.cs", "namespace N { class WidgetThing { } }");
        var b = RepositoryIndexer.Build(repo.Root); b.Text.Dispose(); b.Symbols.Dispose();
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var got = new ManualResetEventSlim(false);
        bool full = false;
        using (var watcher = new RepositoryWatcher(repo.Root, bt => { full |= bt.FullReconcile; foreach (var p in bt.ChangedFullPaths) seen.Add(p); got.Set(); }, debounceMs: 300))
        {
            watcher.Start();
            Directory.Move(repo.FullPath("src/Lib"), repo.FullPath("src/Contoso.Lib"));
            Assert.True(got.Wait(TimeSpan.FromSeconds(15)), "the rename produced no change");
            Thread.Sleep(800); // let a split batch land
        }
        Assert.False(full, "lost events");
        Assert.True(seen.Any(p => p.EndsWith("Contoso.Lib", StringComparison.OrdinalIgnoreCase)), "seen: " + string.Join(", ", seen));

        var u = RepositoryIndexer.UpdatePaths(repo.Root, seen.Distinct().ToList());
        using (u.Text) using (u.Symbols)
        {
            var hits = u.Text.Search("WidgetThing");
            Assert.Contains(hits, h => h.Path.Replace('\\', '/').Contains("src/Contoso.Lib/widget.cs"));
            Assert.DoesNotContain(hits, h => h.Path.Replace('\\', '/').Contains("src/Lib/"));
        }
    }

    // Build output written outside ignored folders (an in-tree make writes .o files next to the sources) is dropped by name,
    // with no disk access on the event thread - a build raises thousands. Once gone it's passed on: it may have been a folder
    // named like one, and the indexer tells the two apart from what it holds.
    [Fact]
    public void Watcher_DropsBuildOutputByName_ButPassesOnWhatsGone()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "namespace N { class A { } }");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var got = new ManualResetEventSlim(false);
        using var watcher = new RepositoryWatcher(repo.Root, b => { foreach (var p in b.ChangedFullPaths) seen.Enqueue(p); got.Set(); }, debounceMs: 300);
        watcher.Start();

        repo.Write("src/a.o", "object code");
        repo.Write("src/b.cs", "namespace N { class B { } }"); // a real change, so a batch comes
        Assert.True(got.Wait(TimeSpan.FromSeconds(15)));
        Thread.Sleep(800);
        Assert.DoesNotContain(seen, p => p.EndsWith("a.o", StringComparison.OrdinalIgnoreCase));

        got.Reset();
        File.Delete(repo.FullPath("src/a.o"));
        Assert.True(got.Wait(TimeSpan.FromSeconds(15)), "the delete produced no change");
        Assert.Contains(seen, p => p.EndsWith("a.o", StringComparison.OrdinalIgnoreCase));
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

    // A flush slower than the max wait (a big reindex under a sync storm): each new event then set the timer due NOW, and
    // every firing parked a pool thread on the flush lock - 2 -> 202 threads in three minutes on a replica, each holding
    // its stack, until the pool starved. Only one flush runs; the rest return, and what arrived meanwhile is delivered by
    // the next one.
    [Fact]
    public void Watcher_SlowFlushUnderAStorm_ParksNoThreads_AndLosesNoEvents()
    {
        using var repo = new TempRepo();
        using var release = new ManualResetEventSlim(false);
        using var firstEntered = new ManualResetEventSlim(false);
        var delivered = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        int flushes = 0;
        using var watcher = new RepositoryWatcher(repo.Root, batch =>
        {
            foreach (var p in batch.ChangedFullPaths) delivered[p] = true;
            if (Interlocked.Increment(ref flushes) == 1) { firstEntered.Set(); release.Wait(TimeSpan.FromSeconds(30)); }
        }, debounceMs: 20);
        watcher.MaxWaitMsForTest = 20;

        watcher.RecordForTest(Path.Combine(repo.Root, "first.cs"));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)), "the first flush never ran");
        int peakInside = 0;
        for (int i = 0; i < 100; i++)
        {
            watcher.RecordForTest(Path.Combine(repo.Root, $"storm{i}.cs"));
            Thread.Sleep(10);
            peakInside = Math.Max(peakInside, watcher.FlushCallbacksInside);
        }
        Assert.True(peakInside <= 2, $"{peakInside} timer callbacks were inside Flush at once (one flush runs; the rest must leave)");

        release.Set();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (delivered.Count < 101 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.Equal(101, delivered.Count); // the storm that arrived during the slow flush still got delivered
    }

    // A sync of millions of files held every path until the debounce ran out. Past the cap the batch is delivered NOW, as
    // paths - not turned into a full reconcile: a network share or huge repo doesn't auto-reconcile, so that would drop
    // the changes and leave the index stale.
    [Fact]
    public void Watcher_PendingReachesTheCap_FlushesThePathsNow()
    {
        using var repo = new TempRepo();
        ChangeBatch? got = null;
        using var flushed = new ManualResetEventSlim(false);
        using var watcher = new RepositoryWatcher(repo.Root, b => { got = b; flushed.Set(); }, debounceMs: 60_000);
        for (int i = 0; i < RepositoryWatcher.MaxPendingPaths; i++)
            watcher.RecordForTest(Path.Combine(repo.Root, $"f{i}.cs"));
        Assert.True(flushed.Wait(TimeSpan.FromSeconds(15)), "no flush at the cap (the debounce is a minute)");
        Assert.False(got!.FullReconcile);
        Assert.Equal(RepositoryWatcher.MaxPendingPaths, got.ChangedFullPaths.Count);
    }
}
