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
