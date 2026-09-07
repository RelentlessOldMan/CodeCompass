using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        repo.Write("src/calc.cpp", """
        int square(int x) { return x * x; }
        int useit() { return square(3); }
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
        Assert.Contains("1 C# +", result);
        // ...but the comment and the "Run" string in the .cs file are NOT counted.
        Assert.DoesNotContain("remember to Run", result);
        Assert.DoesNotContain("var label", result);
        // Lexical fallback still covers non-semantic files (the markdown prose).
        Assert.Contains("docs/notes.md", result);
    }

    [Fact]
    public void FindReferences_SemanticForCpp()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.FindReferences("square");
        Assert.Contains("src/calc.cpp", result);
        Assert.Contains("square(3)", result);
    }

    [Fact]
    public void ConcurrentSearchAndReindex_DoesNotCrash()
    {
        using var repo = NewIndexedRepo();
        int stop = 0;
        Exception? failure = null;

        var searchers = Enumerable.Range(0, 4).Select(threadNo => new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    // Result may be search hits or a status string mid-rebuild; both are fine.
                    _ = CodeCompassTools.SearchCode("Run");
                    _ = CodeCompassTools.FindDefinition("Widget");
                }
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
        })).ToList();

        searchers.ForEach(t => t.Start());
        for (int i = 0; i < 10; i++) CodeCompassTools.Reindex(); // disposes + swaps the mmap index under readers
        Volatile.Write(ref stop, 1);
        searchers.ForEach(t => t.Join());

        Assert.Null(failure); // no ObjectDisposed/AccessViolation from searching a swapped index
    }

    [Fact]
    public void LargeWorkspace_PuntsToCliInsteadOfIndexing()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");
        Environment.SetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB", "0"); // any repo "too large"
        try
        {
            ServerContext.Init(repo.Root);
            var ready = ServerContext.TryGet(out _, out _, out var status);
            Assert.False(ready);
            Assert.Contains("codecompass index", status);
            // The tool relays that message rather than hanging.
            Assert.Contains("codecompass index", CodeCompassTools.SearchCode("A"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB", null);
            ServerContext.Init(repo.Root);
        }
    }

    [Fact]
    public void SmallWorkspace_BackgroundIndexes_ThenServes()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Widget { } }");
        ServerContext.Init(repo.Root);

        bool ready = false;
        string status = "";
        for (int i = 0; i < 200 && !ready; i++)
        {
            ready = ServerContext.TryGet(out _, out _, out status);
            if (!ready) Thread.Sleep(25);
        }

        Assert.True(ready, $"index never became ready; last status: {status}");
        Assert.Contains("Widget", CodeCompassTools.FindDefinition("Widget"));
    }
}
