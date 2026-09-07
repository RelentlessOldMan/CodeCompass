using System.Linq;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Walking;
using Xunit;

namespace CodeCompass.Core.Tests;

public class FileWalkerTests
{
    private static System.Collections.Generic.List<string> WalkRel(string root) =>
        new FileWalker(new IgnoreRules()).Walk(root).Select(f => f.RelativePath).OrderBy(p => p).ToList();

    [Fact]
    public void PrunesIgnoredDirectories()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "x");
        repo.Write("bin/b.cs", "x");
        repo.Write("node_modules/dep/c.js", "x");
        repo.Write(".git/config", "x");
        repo.Write("obj/d.cs", "x");

        Assert.Equal(new[] { "src/a.cs" }, WalkRel(repo.Root));
    }

    [Fact]
    public void NormalizesRelativePathsWithForwardSlashes()
    {
        using var repo = new TempRepo();
        repo.Write("deep/nested/dir/x.cs", "x");

        var rels = WalkRel(repo.Root);
        Assert.Contains("deep/nested/dir/x.cs", rels);
        Assert.DoesNotContain(rels, p => p.Contains('\\'));
    }

    [Fact]
    public void EmptyRepo_ReturnsNothing()
    {
        using var repo = new TempRepo();
        Assert.Empty(WalkRel(repo.Root));
    }
}
