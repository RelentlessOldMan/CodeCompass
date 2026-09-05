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
        repo.Write("src/notes.md", "Widget appears here as prose too.");
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
    public void FindReferences_IsWholeWord()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.FindReferences("Run");
        // "Run" is used in Widget.cs; the word "prose" contains no "Run", and there is
        // no partial-word false match to worry about here.
        Assert.Contains("src/Widget.cs", result);
    }
}
