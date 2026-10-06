using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

// SearchLimits.UnreadablePaths lists candidate files a search could not read, so a reference answer never reads as
// complete when it isn't (review finding 6). Review round 2: large files took a path that swallowed the failure, deleted
// files were reported as "unreadable, rerun", and a network search reported files it read past the point it stopped.
[Collection("compaction-env")] // sets the process-wide CODECOMPASS_FORCE_NETWORK
public class ScanDisclosureTests
{
    private static SegmentedIndex BuildAndLoad(TempRepo repo)
    {
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        symbols.Dispose();
        return text;
    }

    private static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    // A file over the block-index threshold is read through its sidecar / a line stream, not the small-file path.
    [Fact]
    public void LockedLargeFile_IsReportedUnreadable()
    {
        using var repo = new TempRepo();
        var big = new System.Text.StringBuilder();
        while (big.Length < LargeFileIndexer.SidecarThresholdBytes + 4096) big.Append("filler line of text here\n");
        big.Append("int big_probe_fn(void);\n");
        repo.Write("big.c", big.ToString());
        using var text = BuildAndLoad(repo);
        Assert.True(PositionalSidecar.HasSidecar(CodeCompass.Core.Storage.IndexStore.GetCacheDir(repo.Root), "big.c"));

        var limits = new SegmentedIndex.SearchLimits();
        using (Lock(repo.FullPath("big.c")))
            text.Search("big_probe_fn", 100, orderByPath: true, limits: limits);
        Assert.Contains("big.c", limits.UnreadablePaths);
    }

    // A candidate deleted since it was indexed holds no references: nothing is missing, and "rerun" wouldn't change it.
    [Fact]
    public void DeletedCandidate_IsNotReportedUnreadable()
    {
        using var repo = new TempRepo();
        repo.Write("a.c", "int gone_probe_fn(void);\n");
        repo.Write("b.c", "int gone_probe_fn(void);\n");
        using var text = BuildAndLoad(repo);
        File.Delete(repo.FullPath("b.c"));

        var limits = new SegmentedIndex.SearchLimits();
        var hits = text.Search("gone_probe_fn", 100, orderByPath: true, limits: limits);
        Assert.Single(hits);
        Assert.Empty(limits.UnreadablePaths);
    }

    // The network search reads a window of candidates ahead; a file past the point where it had enough results was never
    // needed, so it must not be reported as missing.
    [Fact]
    public void NetworkSearch_FileReadPastTheStop_IsNotReportedUnreadable()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            using var repo = new TempRepo();
            for (int i = 0; i < 4; i++) repo.Write($"f{i}.c", "int ahead_probe_fn(void);\n");
            using var text = BuildAndLoad(repo);

            var limits = new SegmentedIndex.SearchLimits();
            using (Lock(repo.FullPath("f3.c")))
                text.Search("ahead_probe_fn", 1, orderByPath: true, limits: limits);
            Assert.Empty(limits.UnreadablePaths);

            var all = new SegmentedIndex.SearchLimits();
            using (Lock(repo.FullPath("f3.c")))
                text.Search("ahead_probe_fn", 100, orderByPath: true, limits: all);
            Assert.Equal(new[] { "f3.c" }, all.UnreadablePaths);
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", old); }
    }
}
