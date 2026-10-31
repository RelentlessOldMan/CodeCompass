using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// One edited .cs file used to throw the whole C# model away, and the next find_references re-read and re-parsed
// the whole solution (Roslyn: ~20k files). A plain .cs edit, add or delete now updates the model in place; anything that
// can change more than the files named (a project file, a folder, the config) still rebuilds it.
public class CSharpModelUpdateTests
{
    private static List<string> Refs(RoslynCSharpAnalyzer a, string name) =>
        a.FindReferences(name, int.MaxValue).Select(l => $"{l.RelativePath}:{l.Line}:{l.Column}").OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public void AnEditedFile_IsUpdatedInPlace_AndAnswersFromItsNewText()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Equal(new[] { "b.cs:1:27" }, Refs(analyzer, "Ping"));

        repo.Write("b.cs", "class B {\n void N(A a) { }\n void O(A a) { a.Ping(); a.Ping(); } }\n");
        Assert.True(analyzer.TryApplyChanges(new[] { (repo.FullPath("b.cs"), true) }));

        Assert.Equal(new[] { "b.cs:3:18", "b.cs:3:28" }, Refs(analyzer, "Ping"));
        Assert.Equal(1, analyzer.Builds);
        Assert.Equal(1, analyzer.InPlaceUpdates);
    }

    [Fact]
    public void AnAddedAndADeletedFile_AreUpdatedInPlace()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Equal(new[] { "b.cs:1:27" }, Refs(analyzer, "Ping"));

        repo.Write("sub/c.cs", "class C { void N(A a) { a.Ping(); } }\n");
        repo.Delete("b.cs");
        Assert.True(analyzer.TryApplyChanges(new[] { (repo.FullPath("sub/c.cs"), true), (repo.FullPath("b.cs"), false) }));

        Assert.Equal(new[] { "sub/c.cs:1:27" }, Refs(analyzer, "Ping"));
        Assert.Equal(1, analyzer.Builds);
    }

    // A file the walk wouldn't read (the caller says so: the index doesn't hold it - an ignored folder, over the size cap)
    // isn't added; one that stops being read is dropped.
    [Fact]
    public void AFileNoLongerRead_IsDropped_AndOneNeverRead_IsNotAdded()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Equal(new[] { "b.cs:1:27" }, Refs(analyzer, "Ping"));

        repo.Write("bin/gen.cs", "class G { void N(A a) { a.Ping(); } }\n");
        Assert.True(analyzer.TryApplyChanges(new[] { (repo.FullPath("bin/gen.cs"), false), (repo.FullPath("b.cs"), false) }));

        Assert.Empty(Refs(analyzer, "Ping"));
    }

    [Theory]
    [InlineData("App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData(".codecompass.json")]
    public void AProjectFileOrTheConfig_CantBeAppliedInPlace(string rel)
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Refs(analyzer, "Ping");
        repo.Write(rel, "<Project />\n");
        Assert.False(analyzer.TryApplyChanges(new[] { (repo.FullPath("a.cs"), true), (repo.FullPath(rel), true) }));
    }

    // Foo.cs renamed to foo.cs: on Windows both names open the same file, so adding "foo.cs" would hold it twice.
    [Fact]
    public void APathDifferingOnlyInCaseFromAHeldFile_CantBeAppliedInPlace()
    {
        using var repo = new TempRepo();
        repo.Write("Foo.cs", "public class A { public void Ping() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Refs(analyzer, "Ping");
        Assert.False(analyzer.TryApplyChanges(new[] { (repo.FullPath("foo.cs"), true) }));
    }

    [Fact]
    public void AnIncompleteModel_CantBeUpdatedInPlace()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        using (new FileStream(repo.FullPath("b.cs"), FileMode.Open, FileAccess.Read, FileShare.None))
            Refs(analyzer, "Ping");
        Assert.True(analyzer.IsIncomplete);
        repo.Write("a.cs", "public class A { public void Ping() { } void M() { Ping(); } }\n");
        Assert.False(analyzer.TryApplyChanges(new[] { (repo.FullPath("a.cs"), true) }));
    }

    [Fact]
    public void AFileThatCantBeRead_CantBeAppliedInPlace()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Refs(analyzer, "Ping");
        using var locked = new FileStream(repo.FullPath("a.cs"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(analyzer.TryApplyChanges(new[] { (repo.FullPath("a.cs"), true) }));
    }

    [Fact]
    public void ABatchOverTheCap_CantBeAppliedInPlace()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Refs(analyzer, "Ping");
        var many = Enumerable.Range(0, RoslynCSharpAnalyzer.MaxInPlaceChanges + 1).Select(i =>
        {
            repo.Write($"x{i}.cs", $"class X{i} {{ }}\n");
            return (repo.FullPath($"x{i}.cs"), true);
        }).ToList();
        Assert.False(analyzer.TryApplyChanges(many));
    }

    // Paths that are gone and that the model never held change nothing (a build's obj/ churn, ignored): they don't count
    // toward the cap.
    [Fact]
    public void GonePathsTheModelNeverHeld_DontCountTowardTheCap()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Refs(analyzer, "Ping");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        var batch = Enumerable.Range(0, RoslynCSharpAnalyzer.MaxInPlaceChanges + 1).Select(i => (repo.FullPath($"obj/g{i}.cs"), false))
            .Append((repo.FullPath("b.cs"), true)).ToList();
        Assert.True(analyzer.TryApplyChanges(batch), analyzer.RefusedBecause);
        Assert.Equal(new[] { "b.cs:1:27" }, Refs(analyzer, "Ping"));
    }

    // The first .cs in a tree that had none: the framework references, attached only when the walk found C#, come with it.
    [Fact]
    public void TheFirstCSharpFile_BindsAgainstTheFramework()
    {
        using var repo = new TempRepo();
        repo.Write("README.md", "# none yet\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Empty(Refs(analyzer, "WriteLine"));
        repo.Write("a.cs", "class A { void M() { System.Console.WriteLine(1); } }\n");
        Assert.True(analyzer.TryApplyChanges(new[] { (repo.FullPath("a.cs"), true) }));
        Assert.Equal(new[] { "a.cs:1:37" }, Refs(analyzer, "WriteLine"));
    }

    // Per-file answers cached on the model (here: does the file use #if) follow the file's new text.
    [Fact]
    public void ACachedPerFileAnswer_FollowsAnEditedFile()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Empty(analyzer.ConditionalFiles(new[] { repo.FullPath("a.cs") }));
        repo.Write("a.cs", "public class A {\n#if DEBUG\n public void Ping() { }\n#endif\n}\n");
        Assert.True(analyzer.TryApplyChanges(new[] { (repo.FullPath("a.cs"), true) }));
        Assert.Equal(new[] { repo.FullPath("a.cs") }, analyzer.ConditionalFiles(new[] { repo.FullPath("a.cs") }));
    }

    // The model a run of in-place updates ends with answers exactly as one built cold from the same files.
    [Fact]
    public void ARandomRunOfUpdates_AnswersLikeAColdBuild()
    {
        using var repo = new TempRepo();
        var rnd = new Random(20261019);
        string Body(int i)
        {
            var calls = string.Concat(Enumerable.Range(0, rnd.Next(0, 4)).Select(_ => $" new C{rnd.Next(0, 8)}().Ping();"));
            var member = rnd.Next(3) == 0 ? "public void Pong() { Ping(); }" : "";
            return $"public class C{i} {{ public void Ping() {{ }} {member}\n void M() {{{calls} }} }}\n";
        }
        for (int i = 0; i < 5; i++) repo.Write($"f{i}.cs", Body(i));
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Refs(analyzer, "Ping");

        for (int step = 0; step < 25; step++)
        {
            var touched = new HashSet<string>();
            for (int k = rnd.Next(1, 4); k > 0; k--)
            {
                int i = rnd.Next(0, 8);
                var rel = $"{(i % 2 == 0 ? "" : "d/")}f{i}.cs";
                if (File.Exists(repo.FullPath(rel)) && rnd.Next(4) == 0) repo.Delete(rel);
                else repo.Write(rel, Body(i));
                touched.Add(repo.FullPath(rel));
            }
            // As the server sends it: each path once, judged after the batch.
            Assert.True(analyzer.TryApplyChanges(touched.Select(p => (p, File.Exists(p))).ToList()), analyzer.RefusedBecause);
            using var cold = new RoslynCSharpAnalyzer(repo.Root);
            foreach (var name in new[] { "Ping", "Pong", "C3", "C6" })
                Assert.Equal(Refs(cold, name), Refs(analyzer, name));
        }
        Assert.Equal(1, analyzer.Builds);
    }
}
