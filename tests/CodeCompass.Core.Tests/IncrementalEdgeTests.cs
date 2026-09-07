using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// Edit shapes beyond the basic modify/add/delete oracle: rename, delete-then-re-add, and a
// binary file becoming text. Each drives RepositoryIndexer.Update (the full-walk reconcile).
public class IncrementalEdgeTests
{
    private static void BuildAndDispose(string root)
    {
        var b = RepositoryIndexer.Build(root);
        b.Text.Dispose();
        b.Symbols.Dispose();
    }

    [Fact]
    public void FileRename_MovesContentAndSymbol()
    {
        using var repo = new TempRepo();
        repo.Write("old.cs", "namespace N { class RenameMe { } }");
        BuildAndDispose(repo.Root);

        repo.Delete("old.cs");
        repo.Write("new.cs", "namespace N { class RenameMe { } }");

        var (text, symbols, stats) = RepositoryIndexer.Update(repo.Root);
        using (text)
        using (symbols)
        {
            Assert.False(stats.FullRebuild);
            var hits = text.Search("RenameMe").Select(m => m.Path).ToList();
            Assert.Contains("new.cs", hits);
            Assert.DoesNotContain("old.cs", hits);

            var defs = symbols.FindByName("RenameMe");
            Assert.All(defs, d => Assert.Equal("new.cs", d.RelativePath));
            Assert.NotEmpty(defs);
        }
    }

    [Fact]
    public void DeleteThenReAdd_ReplacesOldContent()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaSym { } }");
        BuildAndDispose(repo.Root);

        repo.Delete("a.cs");
        var d = RepositoryIndexer.Update(repo.Root);
        d.Text.Dispose(); d.Symbols.Dispose();

        repo.Write("a.cs", "namespace N { class BetaSym { } }");
        var (text, symbols, _) = RepositoryIndexer.Update(repo.Root);
        using (text)
        using (symbols)
        {
            Assert.Empty(text.Search("AlphaSym"));
            Assert.NotEmpty(text.Search("BetaSym"));
            Assert.Empty(symbols.FindByName("AlphaSym"));
            Assert.NotEmpty(symbols.FindByName("BetaSym"));
        }
    }

    [Fact]
    public void BinaryFileBecomesText_GetsIndexed()
    {
        using var repo = new TempRepo();
        // A NUL byte makes it look binary -> excluded from the index.
        repo.WriteBytes("data.cs", Encoding.UTF8.GetBytes("namespace N { class Hidden { } }\0\0"));
        BuildAndDispose(repo.Root);

        // becomes valid text
        repo.Write("data.cs", "namespace N { class Hidden { } }");
        var (text, symbols, _) = RepositoryIndexer.Update(repo.Root);
        using (text)
        using (symbols)
        {
            Assert.NotEmpty(text.Search("Hidden"));
            Assert.NotEmpty(symbols.FindByName("Hidden"));
        }
    }
}
