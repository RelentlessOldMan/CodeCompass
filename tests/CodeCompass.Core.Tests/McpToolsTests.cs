using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// All MCP tool tests live in one class so they share ServerContext's static state
// serially (xunit runs methods within a class sequentially).
public class McpToolsTests
{
    private static TempRepo NewIndexedRepo()
    {
        var repo = new TempRepo();
        repo.Write("src/Widget.cs", """
        namespace App
        {
            public class Widget
            {
                public void Run() { Helper(); }
                private void Helper() { }
            }
        }
        """);
        repo.Write("src/Caller.cs", """
        namespace App
        {
            public class Caller
            {
                public void Go()
                {
                    var w = new Widget();
                    w.Run();                     // call site
                    // remember to Run it again
                    var label = "Run";
                }
            }
        }
        """);
        repo.Write("docs/notes.md", "Run appears in prose here.");
        ServerContext.Init(repo.Root);
        CodeCompassTools.Reindex();
        return repo;
    }

    [Fact]
    public void SearchCode_FindsMatches()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.SearchCode("Helper");
        Assert.Contains("src/Widget.cs", result);
        Assert.Contains("Helper", result);
    }

    [Fact]
    public void FindDefinition_ReturnsSymbolLocation()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.FindDefinition("Widget");
        Assert.Contains("Class Widget", result);
        Assert.Contains("src/Widget.cs", result);
    }

    [Fact]
    public void FindDefinition_UnknownName_ReportsNothing()
    {
        using var repo = NewIndexedRepo();
        Assert.Contains("No definition", CodeCompassTools.FindDefinition("Nonexistent"));
    }

    [Fact]
    public void SearchSymbols_SubstringCaseInsensitive()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.SearchSymbols("widget");
        Assert.Contains("Widget", result);
    }

    [Fact]
    public void FindReferences_SemanticForCSharp_LexicalForOthers()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.FindReferences("Run");

        // Semantic C#: the real call site is found...
        Assert.Contains("src/Caller.cs", result);
        Assert.Contains("w.Run()", result);
        Assert.Contains("1 semantic C# reference", result);
        // ...but the comment and the "Run" string in the .cs file are NOT counted.
        Assert.DoesNotContain("remember to Run", result);
        Assert.DoesNotContain("var label", result);
        // Lexical fallback still covers non-C# files (the markdown prose).
        Assert.Contains("docs/notes.md", result);
    }
}
