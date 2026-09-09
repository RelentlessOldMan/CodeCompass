using System;
using System.Linq;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// The incremental path appends a segment (and tombstones) per batch; without compaction a
// long-running watch session grows unbounded. NeedsCompaction() flags when the watch orchestration
// should reset via a full rebuild.
public class CompactionTests
{
    [Fact]
    public void IncrementalGrowth_TriggersCompaction_AndRebuildResets()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS");
        Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", "4");
        using var repo = new TempRepo();
        try
        {
            repo.Write("a.cs", "namespace N { class Gen0 { } }");
            var b0 = RepositoryIndexer.Build(repo.Root);
            b0.Text.Dispose(); b0.Symbols.Dispose();

            // Each modify+update appends a segment.
            for (int i = 1; i <= 6; i++)
            {
                repo.Write("a.cs", $"namespace N {{ class Gen{i} {{ }} }}");
                var u = RepositoryIndexer.UpdatePaths(repo.Root, new[] { repo.FullPath("a.cs") });
                u.Text.Dispose(); u.Symbols.Dispose();
            }

            // Segments have piled up past the threshold -> compaction is warranted.
            Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var grown, out var grownSym));
            using (grown)
            using (grownSym)
            {
                Assert.True(grown.SegmentCount >= 4);
                Assert.True(RepositoryIndexer.NeedsCompaction(grown, grownSym));
            }

            // A full rebuild (what the watch orchestration does) resets to a clean, small set.
            var rebuilt = RepositoryIndexer.Build(repo.Root);
            using (rebuilt.Text)
            using (rebuilt.Symbols)
            {
                Assert.False(RepositoryIndexer.NeedsCompaction(rebuilt.Text, rebuilt.Symbols));
                // ...and it still reflects the latest content only.
                Assert.NotEmpty(rebuilt.Text.Search("Gen6"));
                Assert.Empty(rebuilt.Text.Search("Gen0"));
                Assert.NotEmpty(rebuilt.Symbols.FindByName("Gen6"));
                Assert.Empty(rebuilt.Symbols.FindByName("Gen0"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", old);
        }
    }

    [Fact]
    public void Compact_MergesSegments_MatchesFreshRebuild()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Alpha { void One() { } } }");
        repo.Write("b.cs", "namespace N { class Beta { void Two() { } } }");
        repo.Write("c.cs", "namespace N { class Gamma { void Three() { } } }");
        var b0 = RepositoryIndexer.Build(repo.Root);
        b0.Text.Dispose(); b0.Symbols.Dispose();

        // Churn across several batches: modify, delete, add - producing many segments + tombstones.
        for (int i = 1; i <= 5; i++)
        {
            repo.Write("a.cs", $"namespace N {{ class Alpha {{ void One{i}() {{ }} }} }}");
            var u = RepositoryIndexer.UpdatePaths(repo.Root, new[] { repo.FullPath("a.cs") });
            u.Text.Dispose(); u.Symbols.Dispose();
        }
        repo.Delete("b.cs");
        var du = RepositoryIndexer.UpdatePaths(repo.Root, new[] { repo.FullPath("b.cs") });
        du.Text.Dispose(); du.Symbols.Dispose();
        repo.Write("d.cs", "namespace N { class Delta { void Four() { } } }");
        var au = RepositoryIndexer.UpdatePaths(repo.Root, new[] { repo.FullPath("d.cs") });
        au.Text.Dispose(); au.Symbols.Dispose();

        // Compact the on-disk index (merge, no file re-read)...
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        int segBefore = text.SegmentCount;
        text.Compact();
        symbols.Compact();

        using (text)
        using (symbols)
        {
            Assert.True(text.SegmentCount <= segBefore);

            // ...and it must match a fresh rebuild over the same final files exactly.
            var fresh = RepositoryIndexer.Build(repo.Root);
            using (fresh.Text)
            using (fresh.Symbols)
            {
                Assert.Equal(fresh.Text.DocumentCount, text.DocumentCount);
                foreach (var token in new[] { "class", "Alpha", "One5", "Gamma", "Delta", "Beta", "Two" })
                    Assert.Equal(SearchSet(fresh.Text, token), SearchSet(text, token));
                foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "One5", "Four" })
                    Assert.Equal(SymbolSet(fresh.Symbols, name), SymbolSet(symbols, name));
            }
        }
    }

    private static System.Collections.Generic.HashSet<string> SearchSet(
        CodeCompass.Core.Indexing.Segments.SegmentedIndex idx, string query) =>
        idx.Search(query, 1000).Select(m => $"{m.Path}:{m.Line}:{m.Column}:{m.LineText}").ToHashSet();

    private static System.Collections.Generic.HashSet<string> SymbolSet(
        CodeCompass.Core.Symbols.Segments.SegmentedSymbolIndex idx, string name) =>
        idx.FindByName(name).Select(s => $"{s.RelativePath}:{s.Line}:{s.Column}:{s.Kind}:{s.Name}").ToHashSet();

    [Fact]
    public void NeedsCompaction_FalseForFreshBuild()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");
        var b = RepositoryIndexer.Build(repo.Root);
        using (b.Text)
        using (b.Symbols)
            Assert.False(RepositoryIndexer.NeedsCompaction(b.Text, b.Symbols));
    }
}
