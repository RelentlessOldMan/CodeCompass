using CodeCompass.Bench;
using Xunit;

namespace CodeCompass.Core.Tests;

public class VerifierTests
{
    [Fact]
    public void LexicalOracle_MatchesBruteForce_OnTrickyContent()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Foo { void Bar() { Baz(); } } }");
        repo.Write("b.py", "def hello():\n    return 'world'\n# remember to return later\n");
        repo.Write("unicode.txt", "café ünïcödé emoji test\nsecond line\n");
        repo.Write("crlf.txt", "line one\r\nline two\r\nline three\r\n");
        repo.Write("long.cs", "class C { string s = \"" + new string('x', 6000) + "\"; }");

        var result = Verifier.LexicalOracle(repo.Root, budgetBytes: 10 * 1024 * 1024, queryCount: 80);

        Assert.True(result.Queries > 0);
        Assert.Equal(0, result.Mismatches);
    }
}
