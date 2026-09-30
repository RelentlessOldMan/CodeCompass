using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

public class SegmentedIndexTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-idx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static SegmentedIndex Build(TempRepo repo, string dir, long budget)
    {
        var idx = SegmentedIndex.Create(repo.Root, dir, budget);
        foreach (var (rel, full) in repo.Docs())
            idx.AddDocumentText(rel, File.ReadAllText(full));
        idx.Flush();
        return idx;
    }

    private static HashSet<(string, int, int)> BruteForce(TempRepo repo, string query)
    {
        var result = new HashSet<(string, int, int)>();
        foreach (var (rel, full) in repo.Docs())
        {
            var text = File.ReadAllText(full);
            int line = 1, lineStart = 0, scanned = 0, idx;
            while ((idx = text.IndexOf(query, scanned, StringComparison.Ordinal)) >= 0)
            {
                for (int k = scanned; k < idx; k++) if (text[k] == '\n') { line++; lineStart = k + 1; }
                result.Add((rel, line, idx - lineStart + 1));
                scanned = idx + query.Length;
            }
        }
        return result;
    }

    [Fact]
    public void Search_MatchesBruteForce_AcrossManySegments()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Foo { void Run() { } } }");
        repo.Write("b.cs", "namespace N { class Bar { void Run() { } } }");
        repo.Write("c.txt", "run run run\nRun here too\n");
        repo.Write("d.cs", "class Baz { int Value; }");

        var dir = NewTempDir();
        try
        {
            using var idx = Build(repo, dir, budget: 64); // tiny budget -> many segments
            Assert.True(idx.SegmentCount >= 2);

            foreach (var q in new[] { "Run", "run", "class", "Foo", "Value", "nothinghere" })
            {
                var expected = BruteForce(repo, q);
                var actual = idx.Search(q, 1_000_000).Select(m => (m.Path, m.Line, m.Column)).ToHashSet();
                Assert.True(expected.SetEquals(actual), $"mismatch for '{q}': expected {expected.Count}, got {actual.Count}");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    // A whitespace-only query has no meaningful trigrams. Under 3 chars it produces no trigram groups,
    // so the candidate step would treat EVERY file as a candidate and the verify step would match almost
    // every line that contains a space/tab - a context-dump / DoS-shaped footgun (a single space returned
    // 200+ unfiltered lines in the field). The core Search primitive must reject whitespace outright, not
    // just the MCP call site: this is the backstop that protects the CLI and any other caller.
    [Theory]
    [InlineData(" ")]      // single space: length < 3, no trigrams -> would full-scan every file with a space
    [InlineData("  ")]     // two spaces: still under the trigram threshold
    [InlineData("   ")]    // three spaces: has trigrams but is still whitespace-only noise
    [InlineData("\t")]     // tab
    [InlineData(" \t ")]   // mixed whitespace
    public void Search_WhitespaceOnlyQuery_ReturnsEmpty_NoFullScan(string query)
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Foo { void Run() { } } }"); // spaces + a tab-free line
        repo.Write("b.txt", "one two three\nfour\tfive six\n");             // lots of spaces and a tab
        var dir = NewTempDir();
        try
        {
            using var idx = Build(repo, dir, budget: 64);
            Assert.NotEmpty(idx.Search("class")); // sanity: a real token still matches, index isn't just empty
            Assert.Empty(idx.Search(query));      // whitespace-only must not fall to a full-corpus scan
        }
        finally { Directory.Delete(dir, true); }
    }

    // Same backstop for the semantic candidate expansion (find_references / find_callees): a whitespace
    // "symbol" must not expand to the whole corpus and make the analyzer parse every file.
    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void CandidateFiles_WhitespaceOnlyQuery_ReturnsEmpty(string query)
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Foo { void Run() { } } }");
        repo.Write("b.cs", "namespace N { class Bar { void Run() { } } }");
        var dir = NewTempDir();
        try
        {
            using var idx = Build(repo, dir, budget: 64);
            Assert.NotEmpty(idx.CandidateFiles("class")); // sanity
            Assert.Empty(idx.CandidateFiles(query));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RemovePath_TombstonesTheDocument()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class Foo { }");
        repo.Write("b.cs", "class Bar { }");
        var dir = NewTempDir();
        try
        {
            using var idx = Build(repo, dir, budget: 64);
            Assert.NotEmpty(idx.Search("Foo"));

            idx.RemovePath("a.cs");
            idx.Flush();

            Assert.Empty(idx.Search("Foo"));
            Assert.NotEmpty(idx.Search("Bar"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SurvivesFlushAndReopen()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class Foo { }");
        repo.Write("b.cs", "class Bar { }");
        var dir = NewTempDir();
        try
        {
            using (var idx = Build(repo, dir, budget: 64)) { idx.RemovePath("a.cs"); idx.Flush(); }

            using var reopened = SegmentedIndex.Open(repo.Root, dir);
            Assert.Empty(reopened.Search("Foo"));   // tombstone persisted
            Assert.NotEmpty(reopened.Search("Bar"));

            // RemovePath works after reopen (path map rebuilt).
            reopened.RemovePath("b.cs");
            reopened.Flush();
            Assert.Empty(reopened.Search("Bar"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
