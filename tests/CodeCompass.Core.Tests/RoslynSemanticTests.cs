using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

public class RoslynSemanticTests
{
    [Fact]
    public void FindReferences_IsSemantic_NotLexical()
    {
        using var repo = new TempRepo();
        repo.Write("Widget.cs", """
        namespace App;
        public class Widget
        {
            public void Run() { }
        }
        """);
        repo.Write("Caller.cs", """
        namespace App;
        public class Caller
        {
            public void Go()
            {
                var w = new Widget();
                w.Run();                 // real call
                // remember to Run the widget later
                var note = "Run";
            }
        }
        """);

        var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var refs = analyzer.FindReferences("Run");

        // Exactly one true reference: the w.Run() call. The comment mention and the
        // "Run" string literal must NOT be counted (a lexical search would find all three).
        var single = Assert.Single(refs);
        Assert.Equal("Caller.cs", single.RelativePath);
        Assert.Contains("w.Run()", single.LineText);
    }

    [Fact]
    public void FindDefinitions_ResolvesAcrossFiles()
    {
        using var repo = new TempRepo();
        repo.Write("a/Widget.cs", """
        namespace App;
        public class Widget
        {
            public void Run() { }
        }
        """);

        var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var defs = analyzer.FindDefinitions("Widget");

        var def = Assert.Single(defs);
        Assert.Equal("a/Widget.cs", def.RelativePath);
        Assert.Equal(2, def.Line); // class Widget is on line 2
    }

    [Fact]
    public void FindReferences_UnknownSymbol_IsEmpty()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace App; public class A { }");
        var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Empty(analyzer.FindReferences("Nonexistent"));
    }
}
