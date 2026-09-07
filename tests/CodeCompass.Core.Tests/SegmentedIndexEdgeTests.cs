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
}
