using System;
using System.IO;
using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

public class SegmentedIndexEdgeTests
{
    private static string NewCacheDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-segedge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Search_RespectsMaxResults()
    {
        using var repo = new TempRepo();
        repo.Write("f.cs", string.Concat(Enumerable.Repeat("ZEBRA marker\n", 10)));
        var b = RepositoryIndexer.Build(repo.Root);
        using var text = b.Text;
        b.Symbols.Dispose();

        Assert.Equal(3, text.Search("ZEBRA", 3).Count);
        Assert.Equal(10, text.Search("ZEBRA", 100).Count);
    }

    // Query-time ignore consistency: a STALE index built by an older/looser version can still hold postings
    // for paths the current walker would skip (a rival tool's .claude/ cache dump, a node_modules artifact).
    // AddDocumentText plants them directly, exactly as such an index would. Search must NOT return them - the
    // fix drops now-ignored paths at query time, so "ignored at index time" also means "excluded at query
    // time" without a rebuild. (Field report: search leaked .claude/index/tags.json from an old-version index.)
    [Fact]
    public void Search_ExcludesStaleIndexPathsUnderNowIgnoredDirs()
    {
        using var repo = new TempRepo();
        // Write all three to disk (Search verifies by reading the candidate file), then index them MANUALLY -
        // bypassing the walker, which would skip the ignored dirs. That's exactly the shape of a stale index
        // built by an older/looser version that DID index them.
        repo.Write(".claude/index/tags.json", "ZZTOKEN pollution ZZTOKEN everywhere ZZTOKEN");
        repo.Write("node_modules/pkg/x.js", "ZZTOKEN in a dependency");
        repo.Write("src/real.cs", "int ZZTOKEN = 1; // the real one");
        var dir = NewCacheDir();
        using var idx = SegmentedIndex.Create(repo.Root, dir, budget: 1 << 20);
        idx.AddDocumentText(".claude/index/tags.json", File.ReadAllText(repo.FullPath(".claude/index/tags.json")));
        idx.AddDocumentText("node_modules/pkg/x.js", File.ReadAllText(repo.FullPath("node_modules/pkg/x.js")));
        idx.AddDocumentText("src/real.cs", File.ReadAllText(repo.FullPath("src/real.cs")));
        idx.Flush();

        var hits = idx.Search("ZZTOKEN", 100);
        Assert.Contains(hits, h => h.Path == "src/real.cs");
        Assert.DoesNotContain(hits, h => h.Path.Contains(".claude"));
        Assert.DoesNotContain(hits, h => h.Path.Contains("node_modules"));
        Assert.Single(hits); // only the real source file survives the query-time ignore filter
    }

    [Fact]
    public void Search_ShortQueryUnderThreeChars_StillMatches()
    {
        using var repo = new TempRepo();
        repo.Write("f.cs", "if (x) {}\nno match here\na >= b;\n");
        var b = RepositoryIndexer.Build(repo.Root);
        using var text = b.Text;
        b.Symbols.Dispose();

        Assert.NotEmpty(text.Search("if"));  // 2 chars: bypasses trigram filter, scans docs
        Assert.NotEmpty(text.Search(">="));  // 2 chars punctuation
    }

    [Fact]
    public void Search_CrLfContent_ReportsLineAndTrimsCarriageReturn()
    {
        using var repo = new TempRepo();
        repo.WriteBytes("f.cs", Encoding.UTF8.GetBytes("line one\r\nZEBRA here\r\nlast\r\n"));
        var b = RepositoryIndexer.Build(repo.Root);
        using var text = b.Text;
        b.Symbols.Dispose();

        var hits = text.Search("ZEBRA");
        var m = Assert.Single(hits);
        Assert.Equal(2, m.Line);
        Assert.Equal("ZEBRA here", m.LineText); // no trailing '\r'
    }

    [Fact]
    public void RemovePath_OfPendingDoc_ForcesFlushAndTombstones()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "AlphaToken");
        var dir = NewCacheDir();
        try
        {
            using var idx = SegmentedIndex.Create(repo.Root, dir);
            idx.AddDocumentText("a.cs", "AlphaToken"); // NOT flushed
            idx.RemovePath("a.cs");                     // must materialize then tombstone

            Assert.Empty(idx.Search("AlphaToken"));
            Assert.Equal(0, idx.DocumentCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReAddPath_AfterRemove_NewContentSearchable_OldGone()
    {
        using var repo = new TempRepo();
        var dir = NewCacheDir();
        try
        {
            using var idx = SegmentedIndex.Create(repo.Root, dir);
            repo.Write("a.cs", "AlphaToken");
            idx.AddDocumentText("a.cs", "AlphaToken");
            idx.Flush();

            idx.RemovePath("a.cs");
            repo.Write("a.cs", "BetaToken");
            idx.AddDocumentText("a.cs", "BetaToken");
            idx.Flush();

            Assert.Empty(idx.Search("AlphaToken"));
            Assert.NotEmpty(idx.Search("BetaToken"));
            Assert.Equal(1, idx.DocumentCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    // Regression for the UNC find_references count gap (the "744f" field report): a common macro-like
    // name appears in a handful of GIANT files (build-log / disassembly echoes) hundreds of times each,
    // plus a few real references in ordinary source files. The lexical backfill searched with a single
    // GLOBAL result cap applied in candidate order, so the noisy files consumed the whole budget and the
    // real reference was dropped entirely. Worse, candidate order was the index's build/crawl order, which
    // differs between a local disk and a UNC share indexed in separate runs - so a local index returned the
    // real reference (4 hits) while a separately-built UNC index returned 0, with no network read at fault.
    [Fact]
    public void ReferenceSearch_PerFileCap_RescuesFileStarvedByANoisyHighHitFile()
    {
        using var repo = new TempRepo();
        repo.Write("aaa_noise.cpp", string.Concat(Enumerable.Repeat("USE_MACRO(x);\n", 300))); // sorts first
        repo.Write("zzz_real.cpp", "int y = USE_MACRO(k);\n");                                 // sorts last
        var b = RepositoryIndexer.Build(repo.Root);
        using var text = b.Text;
        b.Symbols.Dispose();

        // orderByPath makes the candidate SET build-order-independent (a local and a UNC index agree), which
        // is half the fix. But canonical order alone doesn't rescue the hit: a small global cap is still
        // exhausted by the first (noisy) file, so the real reference in the later file is starved. The bug.
        var starved = text.Search("USE_MACRO", maxResults: 50, orderByPath: true);
        Assert.DoesNotContain(starved, m => m.Path == "zzz_real.cpp");

        // The per-file cap bounds any single file's contribution, leaving budget for every file that
        // references the symbol - so the real reference survives regardless of how noisy its neighbours are.
        var fair = text.Search("USE_MACRO", maxResults: 50, maxPerFile: 8, orderByPath: true);
        Assert.Contains(fair, m => m.Path == "zzz_real.cpp");
        Assert.True(fair.Count(m => m.Path == "aaa_noise.cpp") <= 8, "noisy file must not exceed the per-file cap");
    }
}
