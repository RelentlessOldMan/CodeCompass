using System.IO;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// MCP-C3: the C# name backfill classified each hit file by parsing it again, although the resident model had just parsed
// it. The span filter now borrows the model's tree - but only for the very text it was handed, so an edit since the model
// was built is still judged on the new text.
public class LexicalSpanFilterTreeReuseTests
{
    private const string Source =
        "public class A { public void Ping() { } }\n" +
        "// Ping in a comment\n" +
        "class B { void M() { new A().Ping(); var s = \"Ping\"; } }\n";

    [Fact]
    public void TheModelsTree_IsUsed_WhenItHoldsTheSameText()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", Source);
        var path = Path.GetFullPath(Path.Combine(repo.Root, "a.cs"));
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.NotEmpty(analyzer.FindReferences("Ping")); // the semantic pass parses the candidate

        var filter = new LexicalSpanFilter("Ping") { CSharpTree = analyzer.TreeFor };
        filter.Offer(path, File.ReadAllText(path));
        Assert.True(filter.IsInCommentOrString(path, 2, 4));   // the comment
        Assert.False(filter.IsInCommentOrString(path, 3, 30)); // new A().Ping()
        Assert.True(filter.IsInCommentOrString(path, 3, 47));  // "Ping"
        Assert.Equal(0, filter.FilesParsed);
    }

    [Fact]
    public void AnEditSinceTheModel_IsJudgedOnTheNewText()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", Source);
        var path = Path.GetFullPath(Path.Combine(repo.Root, "a.cs"));
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.NotEmpty(analyzer.FindReferences("Ping"));

        // Line 2 is code now; the model still holds the comment.
        var edited = Source.Replace("// Ping in a comment", "class C { void N() { new A().Ping(); } }");
        var filter = new LexicalSpanFilter("Ping") { CSharpTree = analyzer.TreeFor };
        filter.Offer(path, edited);
        Assert.False(filter.IsInCommentOrString(path, 2, 30));
        Assert.Equal(1, filter.FilesParsed);
    }
}
