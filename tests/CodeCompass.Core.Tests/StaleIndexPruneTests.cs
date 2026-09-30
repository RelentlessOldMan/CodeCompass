using System.Linq;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// Auto-reconcile "suspenders": RepositoryIndexer.PruneIgnored physically drops paths the CURRENT ignore rules
// exclude but a STALE index (built by an older/looser version) still holds - a rival tool's .claude/ cache
// dump, a dir since added to CODECOMPASS_IGNORE. The query-time filter (v1.0.198) already HIDES them; this
// shrinks the on-disk index so it stays clean, with no stat-walk and no source reads. The MCP server runs it
// on startup for the network/huge case where the full reconcile is gated off.
public class StaleIndexPruneTests
{
    [Fact]
    public void PruneIgnored_DropsStaleNowIgnoredPaths_KeepsRealOnes()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "class A { int Keep; }");
        var built = RepositoryIndexer.Build(repo.Root);
        // Simulate a stale index that indexed paths the walker now skips (inject directly, bypassing the walk).
        built.Text.AddDocumentText(".claude/index/tags.json", "Keep Keep Keep pollution");
        built.Text.AddDocumentText("node_modules/pkg/x.js", "Keep in a dependency");
        built.Text.Flush();
        built.Text.Dispose(); built.Symbols.Dispose();

        var u = RepositoryIndexer.PruneIgnored(repo.Root);
        try
        {
            Assert.True(u.Pruned >= 2, $"expected .claude + node_modules pruned; pruned={u.Pruned}");
            var paths = u.Text.AllPaths();
            Assert.DoesNotContain(paths, p => p.Contains(".claude"));
            Assert.DoesNotContain(paths, p => p.Contains("node_modules"));
            Assert.Contains(paths, p => p == "src/a.cs");          // the real file stays
            Assert.Empty(u.Text.Search("Keep", 100).Where(m => m.Path.Contains(".claude") || m.Path.Contains("node_modules")));
        }
        finally { u.Text.Dispose(); u.Symbols.Dispose(); }
    }

    [Fact]
    public void PruneIgnored_CleanIndex_IsNoOp()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "class A { int Keep; }");
        var built = RepositoryIndexer.Build(repo.Root);
        built.Text.Dispose(); built.Symbols.Dispose();

        var u = RepositoryIndexer.PruneIgnored(repo.Root);
        try { Assert.Equal(0, u.Pruned); }                          // a freshly-built index has nothing to prune
        finally { u.Text.Dispose(); u.Symbols.Dispose(); }
    }
}
