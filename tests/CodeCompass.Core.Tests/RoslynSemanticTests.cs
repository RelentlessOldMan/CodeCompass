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
    public void FindReferences_ResolvesAcrossLinkedRoots()
    {
        // The whole point of cross-root semantics: a call in the PROJECT to a type defined in a LINKED
        // root must resolve. A per-root union can't do this (the project compilation has no source for the
        // linked type); one compilation spanning both roots can.
        using var lib = new TempRepo();   // linked external root - defines the type
        using var app = new TempRepo();   // primary project root - uses it
        lib.Write("Gizmo.cs", """
        namespace Ext;
        public class Gizmo { public void Spin() { } }
        """);
        app.Write("Caller.cs", """
        using Ext;
        namespace App;
        public class Caller { public void Go() { var g = new Gizmo(); g.Spin(); } }
        """);

        // roots[0] = primary (app), roots[1] = linked (lib).
        var analyzer = new RoslynCSharpAnalyzer(new[] { app.Root, lib.Root });

        // A reference to the linked-defined method is found in the PROJECT, and it's addressed as
        // primary (empty Root => repo-relative).
        var spin = Assert.Single(analyzer.FindReferences("Spin"));
        Assert.Equal("Caller.cs", spin.RelativePath);
        Assert.Equal("", spin.Root);
        Assert.Contains("g.Spin()", spin.LineText);

        // The type's own definition (in the linked root) is addressed to that root.
        var def = Assert.Single(analyzer.FindDefinitions("Gizmo"));
        Assert.Equal("Gizmo.cs", def.RelativePath);
        Assert.Equal(lib.Root, def.Root); // non-empty => a linked root, shown absolute by the tools
    }

    [Fact]
    public void FindReferences_IgnoresXmlDocCrefMentions()
    {
        // The tool advertises "ignores comments." An XML-doc <see cref="..."/> resolves in Roslyn as a
        // real reference, but it lives in a comment - so it must NOT be counted, only the real call is.
        using var repo = new TempRepo();
        repo.Write("Widget.cs", """
        namespace App;
        public class Widget { public void Run() { } }
        """);
        repo.Write("Caller.cs", """
        namespace App;
        /// <summary>Uses <see cref="Widget"/> to do work.</summary>
        public class Caller
        {
            public void Go() { var w = new Widget(); w.Run(); }
        }
        """);

        var refs = new RoslynCSharpAnalyzer(repo.Root).FindReferences("Widget");
        // Only the real `new Widget()` usage - the <see cref="Widget"/> in the doc comment is excluded.
        var single = Assert.Single(refs);
        Assert.Equal("Caller.cs", single.RelativePath);
        Assert.Contains("new Widget()", single.LineText);
    }

    [Fact]
    public void FindCallees_ResolvesRealTargets_IgnoringOverloadsAndFramework()
    {
        // The differentiator vs a syntactic call graph: w.Run() must bind to Widget.Run only (not the
        // same-named Other.Run), and w.ToString() (framework) must be omitted. A syntactic graph returns
        // every same-named symbol - the ~2% precision problem the benchmark documented.
        using var repo = new TempRepo();
        repo.Write("Widget.cs", """
        namespace App;
        public class Widget
        {
            public void Run() { }
        }
        public class Other
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
                w.Run();       // real callee: Widget.Run
                w.ToString();  // framework call - must be omitted
            }
        }
        """);

        var callees = new RoslynCSharpAnalyzer(repo.Root).FindCallees("Go");
        Assert.Contains(callees, c => c.RelativePath == "Widget.cs" && c.LineText.Contains("Run"));
        // Exactly one Run - the Other.Run overload is NOT returned (semantic, not syntactic).
        Assert.Single(callees.Where(c => c.LineText.Contains("Run")));
        // Framework/external calls (ToString) are omitted.
        Assert.DoesNotContain(callees, c => c.LineText.Contains("ToString"));
    }

    [Fact]
    public void FindCallees_ResolvesAcrossFiles()
    {
        using var repo = new TempRepo();
        repo.Write("Service.cs", """
        namespace App;
        public class Service { public void Handle() { new Repo().Save(); } }
        """);
        repo.Write("Repo.cs", """
        namespace App;
        public class Repo { public void Save() { } }
        """);

        var callees = new RoslynCSharpAnalyzer(repo.Root).FindCallees("Handle");
        // The callee's DEFINITION resolves to the OTHER file, so the next hop is a direct jump.
        Assert.Contains(callees, c => c.RelativePath == "Repo.cs" && c.LineText.Contains("Save"));
    }

    [Fact]
    public void FindCallees_HandlesRecursionAndUnknown_WithoutError()
    {
        using var repo = new TempRepo();
        repo.Write("R.cs", """
        namespace App;
        public class R { public int Fac(int n) { return n <= 1 ? 1 : n * Fac(n - 1); } }
        """);
        var analyzer = new RoslynCSharpAnalyzer(repo.Root);

        // A self-recursive method lists itself once (deduped by definition), no infinite loop.
        var self = analyzer.FindCallees("Fac");
        Assert.Single(self.Where(c => c.LineText.Contains("Fac")));

        // An unknown name is simply empty - never throws.
        Assert.Empty(analyzer.FindCallees("NoSuchMethod"));
    }

    [Fact]
    public void FindReferences_UnknownSymbol_IsEmpty()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace App; public class A { }");
        var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Empty(analyzer.FindReferences("Nonexistent"));
    }

    [Fact]
    public void Dispose_ReleasesModel_AndRebuildsLazilyOnReuse()
    {
        using var repo = new TempRepo();
        repo.Write("Widget.cs", "namespace App; public class Widget { public void Run() { } }");
        repo.Write("Caller.cs", "namespace App; public class Caller { public void Go() { new Widget().Run(); } }");

        var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var before = analyzer.FindReferences("Run").Select(r => (r.RelativePath, r.Line, r.Column)).ToList();
        Assert.NotEmpty(before);

        // Idle-eviction disposes the resident solution to free memory; a subsequent query must rebuild
        // it lazily and return byte-identical results (proves Dispose doesn't corrupt reusable state).
        analyzer.Dispose();
        var after = analyzer.FindReferences("Run").Select(r => (r.RelativePath, r.Line, r.Column)).ToList();
        Assert.Equal(before, after);
    }
}
