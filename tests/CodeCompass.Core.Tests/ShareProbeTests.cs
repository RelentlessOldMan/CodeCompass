using System;
using System.IO;
using System.Linq;
using System.Threading;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// File.Exists and Directory.Exists answer false when the call FAILS, too. On a network root a share that blinked was taken
// as "gone", dropping healthy files from the index - a changed path, or a file a folder's walk didn't list. There, "gone"
// must be the disk's answer; a path it can't answer for stays as indexed and is retried.
[Collection("compaction-env")] // sets the process-wide CODECOMPASS_FORCE_NETWORK and CODECOMPASS_COMPACT_SEGMENTS
public class ShareProbeTests
{
    private static ChangeCounts Apply(TempRepo repo, bool network, Func<string, bool> fails, params string[] changed)
    {
        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", network ? "1" : "0");
        RepositoryIndexer.ProbeFailsForTests = fails;
        try
        {
            Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
            using (text) using (symbols) using (var snapshot = RepositoryIndexer.LoadSnapshot(repo.Root))
            {
                var c = RepositoryIndexer.ApplyChanges(text, symbols, snapshot, repo.Root, changed.Select(repo.FullPath));
                RepositoryIndexer.Persist(repo.Root, text, symbols, snapshot);
                return c;
            }
        }
        finally
        {
            RepositoryIndexer.ProbeFailsForTests = null;
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev);
        }
    }

    private static bool Indexed(TempRepo repo, string symbol)
    {
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        using (text) using (symbols) return symbols.FindByName(symbol).Any();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AChangedPathTheShareCantAnswerFor_IsKeptAndReportedUnsure_ButALocalDiskIsBelieved(bool network)
    {
        using var repo = new TempRepo();
        repo.Write("keep.cs", "namespace N { class KeptOnShare { } }");
        var b = RepositoryIndexer.Build(repo.Root); b.Text.Dispose(); b.Symbols.Dispose();

        var c = Apply(repo, network, p => p.EndsWith("keep.cs"), "keep.cs");

        Assert.Equal(network, Indexed(repo, "KeptOnShare"));
        Assert.Equal(network ? new[] { repo.FullPath("keep.cs") } : Array.Empty<string>(), c.Unsure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFolderSweep_DropsWhatsConfirmedGone_ButKeepsWhatTheShareCantAnswerFor(bool network)
    {
        using var repo = new TempRepo();
        repo.Write("dir/x.cs", "namespace N { class StaysListed { } }");
        repo.Write("dir/y.cs", "namespace N { class ShareBlinked { } }");
        repo.Write("dir/z.cs", "namespace N { class ReallyDeleted { } }");
        var b = RepositoryIndexer.Build(repo.Root); b.Text.Dispose(); b.Symbols.Dispose();
        // The folder was replaced: its listing misses y.cs (the share blinked) and z.cs (deleted).
        File.Delete(repo.FullPath("dir/y.cs"));
        File.Delete(repo.FullPath("dir/z.cs"));

        var c = Apply(repo, network, p => p.EndsWith("y.cs"), "dir");

        Assert.True(Indexed(repo, "StaysListed"));
        Assert.False(Indexed(repo, "ReallyDeleted"));
        Assert.Equal(network, Indexed(repo, "ShareBlinked"));
        Assert.Equal(network ? new[] { repo.FullPath("dir/y.cs") } : Array.Empty<string>(), c.Unsure);
    }

    // The sweep took a file that's now a same-named folder for gone, and dropped it WITH its children - the files the walk
    // had just indexed in that folder.
    [Fact]
    public void AFolderSweep_AFileReplacedByASameNamedFolder_KeepsWhatTheWalkFoundInIt()
    {
        using var repo = new TempRepo();
        repo.Write("dir/thing.cs", "namespace N { class OldThing { } }");
        var b = RepositoryIndexer.Build(repo.Root); b.Text.Dispose(); b.Symbols.Dispose();
        File.Delete(repo.FullPath("dir/thing.cs"));
        repo.Write("dir/thing.cs/inner.cs", "namespace N { class InnerThing { } }");

        Apply(repo, network: false, _ => false, "dir");

        Assert.False(Indexed(repo, "OldThing"));
        Assert.True(Indexed(repo, "InnerThing"));
        using var snapshot = RepositoryIndexer.LoadSnapshot(repo.Root); // what the next update diffs against
        Assert.False(snapshot.TryGetValue("dir/thing.cs", out _));
        Assert.True(snapshot.TryGetValue("dir/thing.cs/inner.cs", out _));
    }

    // Found by the test above: removing a path tombstoned only its symbols already written to a segment. Ones still
    // pending (indexed earlier in the same batch) stayed, so find_definition kept answering with a file that's gone.
    [Fact]
    public void SymbolIndex_RemovingAPathIndexedInTheSameBatch_DropsItsPendingSymbols()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-pend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var idx = CodeCompass.Core.Symbols.Segments.SegmentedSymbolIndex.Create(dir, 1L << 20);
            idx.Add(new CodeCompass.Core.Symbols.Symbol("KeptPending", CodeCompass.Core.Symbols.SymbolKind.Class, "a.cs", 1, 1));
            idx.Add(new CodeCompass.Core.Symbols.Symbol("GonePending", CodeCompass.Core.Symbols.SymbolKind.Class, "b.cs", 1, 1));
            idx.RemovePath("b.cs");
            idx.Add(new CodeCompass.Core.Symbols.Symbol("LaterPending", CodeCompass.Core.Symbols.SymbolKind.Class, "c.cs", 1, 1));
            idx.Flush();

            Assert.Empty(idx.FindByName("GonePending"));
            Assert.NotEmpty(idx.FindByName("KeptPending"));
            Assert.NotEmpty(idx.FindByName("LaterPending"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // A path the share didn't answer for is retried, and dropped once the share says it's gone.
    [Fact]
    public void Server_APathTheShareCantAnswerFor_IsRetried_AndDroppedOnceConfirmedGone()
    {
        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaStays { } }");
        repo.Write("gone.cs", "namespace N { class GoneLater { } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            File.Delete(repo.FullPath("gone.cs"));
            int blinks = 0;
            RepositoryIndexer.ProbeFailsForTests = p => p.EndsWith("gone.cs") && Interlocked.Increment(ref blinks) <= 1;

            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("gone.cs") }, FullReconcile: false));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Indexed(repo, "GoneLater") && DateTime.UtcNow < deadline) Thread.Sleep(100);
            Assert.False(Indexed(repo, "GoneLater"));
            Assert.True(blinks >= 2); // the first probe failed: it was the retry that dropped it
            Assert.True(Indexed(repo, "AlphaStays"));
        }
        finally
        {
            RepositoryIndexer.ProbeFailsForTests = null;
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev);
            ServerContext.Init(repo.Root);
        }
    }

    // A compaction (and a lost-events rebuild) set the server to Building, so every query answered "indexing 0%" for as
    // long as the merge took, though the loaded index was fine to serve - as it is during any other rebuild here.
    [Theory]
    [InlineData(false)] // segments piled up: a compaction
    [InlineData(true)]  // the watcher lost events: a full rebuild
    public void Server_WhileACompactionOrRebuildRuns_QueriesKeepAnswering(bool lostEvents)
    {
        var prevCompact = Environment.GetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS");
        Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", "2");
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        ServerContext.Init(repo.Root);
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        try
        {
            CodeCompassTools.Reindex();
            void Hold() { held.Set(); release.Wait(TimeSpan.FromSeconds(30)); }
            if (lostEvents) RepositoryIndexer.OnFileStartForTests = _ => Hold();
            else RepositoryIndexer.OnCompactForTests = Hold;

            repo.Write("b.cs", "namespace N { class BravoLocal { } }");
            var write = new Thread(() => ServerContext.OnChangesForTest(
                new ChangeBatch(lostEvents ? Array.Empty<string>() : new[] { repo.FullPath("b.cs") }, FullReconcile: lostEvents)));
            write.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the merge/rebuild never started");

            var during = CodeCompassTools.SearchCode("AlphaLocal");
            release.Set();
            Assert.True(write.Join(TimeSpan.FromSeconds(30)));

            Assert.Contains("a.cs", during);
            Assert.DoesNotContain("indexing", during);
            Assert.Contains("b.cs", CodeCompassTools.SearchCode("BravoLocal"));

            // ...and once it's done, edits apply again (not parked as if it still ran).
            repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));
            Assert.Contains("c.cs", CodeCompassTools.SearchCode("CharlieLocal"));
        }
        finally
        {
            release.Set();
            RepositoryIndexer.OnCompactForTests = null;
            RepositoryIndexer.OnFileStartForTests = null;
            Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", prevCompact);
            ServerContext.Init(repo.Root);
        }
    }
}
