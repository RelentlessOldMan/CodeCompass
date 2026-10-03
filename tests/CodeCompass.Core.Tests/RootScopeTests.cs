using System.IO;
using System.Linq;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Unit coverage for the focus-token resolver behind manage_links action=focus (the "search only repo A"
// scope selector). Pure + case-insensitive; matching precedence is exact-path -> basename -> substring,
// and a substring token is deliberately allowed to select SEVERAL roots at once ("focus both").
public class RootScopeTests
{
    // Build platform-correct absolute roots so the test runs on any drive/mount.
    private static string R(params string[] parts) => RootScope.Normalize(Path.Combine(Path.GetTempPath(), Path.Combine(parts)));

    [Fact]
    public void Match_ByBasename_SelectsThatRoot()
    {
        var roots = new[] { R("big", "repoA"), R("big", "repoB"), R("wrapper") };
        var m = RootScope.Match(roots, "repoA");
        Assert.Equal(new[] { R("big", "repoA") }, m);
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var roots = new[] { R("big", "RepoA"), R("big", "repoB") };
        Assert.Equal(new[] { R("big", "RepoA") }, RootScope.Match(roots, "REPOA"));
    }

    [Fact]
    public void Match_ExactAbsolutePath_Wins()
    {
        var roots = new[] { R("big", "repoA"), R("big", "repoB") };
        var token = Path.Combine(Path.GetTempPath(), "big", "repoA"); // rooted, maybe with trailing-sep variance
        Assert.Equal(new[] { R("big", "repoA") }, RootScope.Match(roots, token));
    }

    [Fact]
    public void Match_SharedParentSubstring_SelectsSeveral()
    {
        // "focus both": a parent-folder token matches every root beneath it (substring stage).
        var roots = new[] { R("big", "repoA"), R("big", "repoB"), R("other", "repoC") };
        var m = RootScope.Match(roots, "big");
        Assert.Equal(2, m.Count);
        Assert.Contains(R("big", "repoA"), m);
        Assert.Contains(R("big", "repoB"), m);
        Assert.DoesNotContain(R("other", "repoC"), m);
    }

    [Fact]
    public void Match_BasenameBeatsSubstring()
    {
        // A token equal to one root's folder name resolves to JUST that root, even though it is also a
        // substring of a sibling's path - exact-folder intent shouldn't drag in incidental substring hits.
        var roots = new[] { R("svc"), R("svc-legacy") };
        Assert.Equal(new[] { R("svc") }, RootScope.Match(roots, "svc"));
    }

    [Fact]
    public void Match_RootedToken_NoExactHit_FallsThroughToSubstring()
    {
        // An absolute path that is NOT exactly a root (here a parent directory) must fall past the exact-path
        // stage and still resolve - exercises the rooted-but-not-exact branch.
        var roots = new[] { R("big", "repoA"), R("other", "repoB") };
        var token = Path.Combine(Path.GetTempPath(), "big"); // rooted, equals no root
        Assert.Equal(new[] { R("big", "repoA") }, RootScope.Match(roots, token));
    }

    [Fact]
    public void Match_NoHit_IsEmpty_NotThrow()
    {
        var roots = new[] { R("big", "repoA") };
        Assert.Empty(RootScope.Match(roots, "nonsense"));
        Assert.Empty(RootScope.Match(roots, "   "));
        Assert.Empty(RootScope.Match(System.Array.Empty<string>(), "repoA"));
    }
}
