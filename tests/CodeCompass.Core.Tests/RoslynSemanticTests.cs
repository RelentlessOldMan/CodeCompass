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

    // #3 (v1.0.216): callees hidden in inactive #if/#elif branches. Roslyn parses them as disabled text, so the
    // semantic FindCallees never sees them; FindCalleesInInactiveBranches re-lexes the disabled region and
    // resolves each discovered NAME in-repo. In-process coverage of the recovery arms (simple invocation,
    // member-access, object-creation with identifier/generic/qualified type names) and the max cap - the Cli_*
    // subprocess tests prove the end-to-end wiring but coverlet can't instrument a child process.
    [Fact]
    public void FindCalleesInInactiveBranches_RecoversGuardedCalls_ByName()
    {
        using var repo = new TempRepo();
        repo.Write("Targets.cs", """
        namespace App;
        public class Gizmo { public void Spin() { } }
        public class Box<T> { }
        """);
        repo.Write("Login.cs", """
        namespace App;
        public class Login
        {
            public void Go()
            {
        #if NET6_0_OR_GREATER
                Assist();                 // simple invocation -> Assist
                var g = new Gizmo();      // object creation, identifier type -> Gizmo
                g.Spin();                 // member-access callee -> Spin
                var b = new Box<int>();   // object creation, generic type -> Box
                var g2 = new App.Gizmo(); // object creation, qualified type -> Gizmo
                a[0]();                   // element-access invocation -> no simple name (skipped)
                var z = new int();        // predefined-type construction -> no simple name (skipped)
        #endif
            }
            public void Assist() { }
        }
        """);

        var analyzer = new RoslynCSharpAnalyzer(repo.Root);

        // The semantic pass is blind to everything inside the inactive branch.
        Assert.DoesNotContain(analyzer.FindCallees("Go"), c => c.LineText.Contains("Spin") || c.LineText.Contains("Gizmo"));

        var recovered = analyzer.FindCalleesInInactiveBranches("Go");
        Assert.Contains(recovered, c => c.LineText.Contains("Assist"));  // simple invocation recovered by name
        Assert.Contains(recovered, c => c.LineText.Contains("Spin"));    // member-access callee recovered by name
        Assert.Contains(recovered, c => c.LineText.Contains("Gizmo"));   // object creation (identifier + qualified type)
        Assert.Contains(recovered, c => c.LineText.Contains("Box"));     // object creation, generic type

        // The max cap is honored - recovery stops once the budget is reached.
        Assert.Single(analyzer.FindCalleesInInactiveBranches("Go", max: 1));
    }

    [Fact]
    public void FindCalleesInInactiveBranches_CleanMethod_IsEmpty()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace App; public class C { public void M() { System.Console.WriteLine(); } }");
        var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        // No disabled region => nothing to recover (callers gate on conditional compilation and skip the work).
        Assert.Empty(analyzer.FindCalleesInInactiveBranches("M"));
        // Unknown name => empty, never throws.
        Assert.Empty(analyzer.FindCalleesInInactiveBranches("NoSuchMethod"));
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

    // `refs Equals` on EF Core ran 20+ minutes without finishing. Every `override Equals` is linked to all the others
    // through object.Equals, and Roslyn's search cascades along that link - so one search per declaration (547 in EF Core)
    // re-found every Equals use in the solution each time. A declaration that an earlier search already covered (it came
    // back as one of that search's definitions) must not be searched again; the answer must stay the same.
    // (Framework members named Equals - MemoryExtensions.Equals, Vector.Equals, ... - are distinct symbols and are each
    // searched once whatever the repo holds, so the invariant is "the count does not grow with the overrides".)
    [Fact]
    public void FindReferences_OverridesOfOneMember_AreSearchedOnce()
    {
        var (fewRefs, fewSearches) = EqualsRefs(classes: 5);
        var (manyRefs, manySearches) = EqualsRefs(classes: 40);

        Assert.Equal(5 + 1, fewRefs);
        Assert.Equal(40 + 1, manyRefs);
        Assert.Equal(fewSearches, manySearches);
    }

    // `refs GetEnumerator` on Roslyn took 514 s: ~100 unrelated declarations, each a whole-solution search, and Roslyn holds
    // a document's SemanticModel (with the binding it has done) only weakly - so between searches the GC took the models
    // and every search re-bound every candidate document (for GetEnumerator, every foreach). When one call runs several
    // searches it must keep the models alive across them; a single search has nothing to share and must not pay for it.
    [Fact]
    public void FindReferences_SeveralSearches_ShareSemanticModels()
    {
        using var repo = new TempRepo();
        foreach (var type in new[] { "Bag", "Ring" })
            repo.Write($"{type}.cs", $$"""
            namespace App;
            public class {{type}}
            {
                public System.Collections.Generic.List<int>.Enumerator GetEnumerator() => new System.Collections.Generic.List<int>().GetEnumerator();
            }
            """);
        repo.Write("Use.cs", """
        namespace App;
        public static class Use
        {
            public static void Run(Bag b, Ring r)
            {
                var e1 = b.GetEnumerator(); // CALL
                var e2 = r.GetEnumerator(); // CALL
            }
            public static int OnceOnlyZq() => 1;
            public static int Twice() => OnceOnlyZq(); // ONCE
        }
        """);

        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var searches = 0;
        var survivedEveryGc = true;
        analyzer.AfterSearch = solution =>
        {
            searches++;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            survivedEveryGc &= solution.Projects.SelectMany(p => p.Documents).All(d => d.TryGetSemanticModel(out _));
        };
        var several = analyzer.FindReferences("GetEnumerator", max: 1000);
        Assert.Equal(2, several.Count(r => r.LineText.Contains("// CALL")));
        Assert.True(searches > 1);
        Assert.True(survivedEveryGc, "a document's SemanticModel was collected between searches - the next search re-binds it");
        Assert.Equal(3, analyzer.LastPinnedModels); // Bag.cs, Ring.cs, Use.cs

        analyzer.AfterSearch = null;
        var single = analyzer.FindReferences("OnceOnlyZq");
        Assert.Single(single, r => r.LineText.Contains("// ONCE"));
        Assert.Equal(0, analyzer.LastPinnedModels);
    }

    private static (int CallSites, int Searches) EqualsRefs(int classes)
    {
        using var repo = new TempRepo();
        for (int i = 0; i < classes; i++)
            repo.Write($"T{i}.cs", $$"""
            namespace App;
            public class T{{i}}
            {
                public override bool Equals(object? o) => o is T{{i}};
                public override int GetHashCode() => {{i}};
                public static bool Same(T{{i}} a, object b) => a.Equals(b); // CALL
            }
            """);
        repo.Write("Plain.cs", """
        namespace App;
        public class Plain
        {
            public bool Equals(Plain other) => true;
            public static bool Use(Plain p) => p.Equals(p); // CALL
        }
        """);

        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var refs = analyzer.FindReferences("Equals", max: 1000);
        return (refs.Count(r => r.LineText.Contains("// CALL")), analyzer.ReferenceSearches);
    }
}
