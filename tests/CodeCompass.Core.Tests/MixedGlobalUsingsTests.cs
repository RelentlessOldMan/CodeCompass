using System.IO;
using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// The analyzer builds one Roslyn project for the whole tree, and the global usings of SDK projects with <ImplicitUsings>
// apply to every document. A legacy project in the same repo (no implicit usings) that has its own type named like one
// of those namespaces' types (Task, Timer, File, Path, Thread) then sees it as ambiguous (CS0104): the variable binds to
// an error type and references through it vanish from the semantic pass. Like #if-guarded code, such a file is
// backfilled by name and disclosed: only that file when the clash is in its code, every C# file when it is in a
// declaration (a field typed Task reaches other files).
public class MixedGlobalUsingsTests
{
    private static TempRepo MixedRepo(bool clash, bool declarationClash = false)
    {
        var repo = new TempRepo();
        repo.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        repo.Write("App/Widget.cs", """
            namespace App;
            public sealed class Widget { public int Spin() => 1; }
            public static class UseW { public static void All(List<Widget> ws) { foreach (var w in ws) w.Spin(); } }
            """);
        // A name match no semantic pass resolves (an unknown receiver type): only a repo-wide backfill would list it.
        repo.Write("App/Broken.cs", """
            namespace App;
            public static class Broken { public static void Go(Unknown u) { u.RunJob(); } }
            """);
        repo.Write("Legacy/Legacy.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
            </Project>
            """);
        string type = clash ? "Task" : "Job";
        repo.Write("Legacy/Jobs.cs", "namespace Legacy.Jobs\n{\n    public class " + type + " { public void RunJob() { } }\n}\n");
        repo.Write("Legacy/Use.cs",
            "using System;\nusing Legacy.Jobs;\nnamespace Legacy\n{\n" +
            "    public static class Runner { public static void Go() { var t = new " + type + "(); t.RunJob(); } }\n}\n");
        if (declarationClash)
        {
            repo.Write("Legacy/Holder.cs", "using Legacy.Jobs;\nnamespace Legacy\n{\n    public class Holder { public Task Job; }\n}\n");
            repo.Write("Legacy/Caller.cs",
                "namespace Legacy\n{\n    public static class Caller { public static void Go(Holder h) { h.Job.RunJob(); } }\n}\n");
        }
        return repo;
    }

    private static string Mcp(TempRepo repo, string name)
    {
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            return CodeCompass.Mcp.CodeCompassTools.FindReferences(name);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    // The per-file clash check binds the file (GetDiagnostics). The analyzer is dropped whenever its sources change, so the
    // answer is fixed for its lifetime: compute it once per file, not on every find_references.
    [Fact]
    public void Analyzer_ChecksEachFileForAClash_OncePerAnalyzer()
    {
        using var repo = MixedRepo(clash: true);
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var candidates = new[] { "Legacy/Use.cs", "Legacy/Jobs.cs" }.Select(repo.FullPath).ToList();
        var first = analyzer.UsingsClash(candidates);
        int runs = analyzer.ClashFileChecks;
        Assert.True(runs > 0);
        var second = analyzer.UsingsClash(candidates);
        Assert.Equal(runs, analyzer.ClashFileChecks);
        Assert.Equal(first.Files, second.Files);

        analyzer.Dispose(); // rebuilds lazily on next use: the per-file answers belong to the old model
        Assert.Equal(0, analyzer.CachedFileAnswers); // ...and are released with it, not held until the next use
        analyzer.UsingsClash(candidates);
        Assert.Equal(2 * runs, analyzer.ClashFileChecks);
    }

    // Binding is what costs (a Roslyn-size repo has ~13.5k files outside the usings): a file that names nothing two of its
    // imports declare can't have a CS0104, so it isn't bound to find that out - on any query, cold or warm.
    [Fact]
    public void Analyzer_BindsOnlyFiles_NamingWhatTwoOfTheirImportsDeclare()
    {
        using var repo = MixedRepo(clash: true);
        repo.Write("Legacy/Quiet.cs", "using Legacy.Jobs;\nnamespace Legacy\n{\n    public static class Quiet { public static int Go() => 1; }\n}\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var (declarations, files) = analyzer.UsingsClash(new[] { "Legacy/Use.cs", "Legacy/Quiet.cs" }.Select(repo.FullPath).ToList());
        Assert.Empty(declarations);
        Assert.Equal(new[] { repo.FullPath("Legacy/Use.cs") }, files);
        // Use.cs imports Legacy.Jobs and (globally) System.Threading.Tasks, both declaring Task, and names it: bound for its
        // declarations and, as a candidate, in full. Quiet.cs has the same imports but never says Task; Jobs.cs declares a
        // Task but imports only the global namespaces, which share no name. Neither is bound.
        Assert.Equal(2, analyzer.ClashBinds);
    }

    // The name set isn't only the injected namespaces' types: the one compilation lets two of a legacy file's own usings
    // clash too (here an attribute, used without its suffix), and that hides references just the same.
    [Fact]
    public void Analyzer_ReportsAClash_BetweenTwoOwnUsings_ThroughAnAttributeName()
    {
        using var repo = MixedRepo(clash: false);
        repo.Write("Legacy/Marks.cs",
            "namespace Legacy.A\n{\n    public class MarkAttribute : System.Attribute { }\n}\n" +
            "namespace Legacy.B\n{\n    public class MarkAttribute : System.Attribute { }\n}\n");
        repo.Write("Legacy/Marked.cs",
            "using Legacy.A;\nusing Legacy.B;\nnamespace Legacy\n{\n    public static class Marked { [Mark] public static void Go() { } }\n}\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var (_, files) = analyzer.UsingsClash(new[] { repo.FullPath("Legacy/Marked.cs") });
        Assert.Equal(new[] { repo.FullPath("Legacy/Marked.cs") }, files);
    }

    // `using static` imports a type's nested types, so one of those can clash with an imported namespace's type too.
    [Fact]
    public void Analyzer_ReportsAClash_ThroughAUsingStaticNestedType()
    {
        using var repo = MixedRepo(clash: false);
        // A nested static target (Outer.Inner): resolved through a type, not only a namespace.
        repo.Write("Legacy/Outer.cs", "namespace Legacy.Deep\n{\n    public static class Outer { public static class Inner { public class Task { } } }\n}\n");
        repo.Write("Legacy/Nested.cs",
            "using static Legacy.Deep.Outer.Inner;\nnamespace Legacy\n{\n    public static class Nested { public static object Go() => new Task(); }\n}\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var (_, files) = analyzer.UsingsClash(new[] { repo.FullPath("Legacy/Nested.cs") });
        Assert.Equal(new[] { repo.FullPath("Legacy/Nested.cs") }, files);
    }

    // Gen<int>.Item and Gen<string>.Item are different types: two static imports of one generic type can clash.
    [Fact]
    public void Analyzer_ReportsAClash_BetweenTwoConstructionsOfOneGenericType()
    {
        using var repo = MixedRepo(clash: false);
        repo.Write("Legacy/Gen.cs", "namespace Legacy.Deep\n{\n    public class Gen<T> { public class Item { } }\n}\n");
        repo.Write("Legacy/TwoGen.cs",
            "using static Legacy.Deep.Gen<int>;\nusing static Legacy.Deep.Gen<string>;\nnamespace Legacy\n{\n" +
            "    public static class TwoGen { public static object Go() => new Item(); }\n}\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var (_, files) = analyzer.UsingsClash(new[] { repo.FullPath("Legacy/TwoGen.cs") });
        Assert.Equal(new[] { repo.FullPath("Legacy/TwoGen.cs") }, files);
    }

    // A using inside a namespace body is resolved from that namespace (relative names, outer aliases, outer imports), not
    // from the top - so `using Jobs;` in namespace Legacy is Legacy.Jobs. Each of these is a real CS0104.
    [Theory]
    [InlineData("relative", "namespace Legacy\n{\n    using Jobs;\n    using System.Threading.Tasks;\n    public static class R { public static object Go() => new Task(); }\n}\n")]
    [InlineData("file-scoped", "namespace Legacy;\nusing Jobs;\nusing System.Threading.Tasks;\npublic static class R { public static object Go() => new Task(); }\n")]
    [InlineData("outer alias", "using L = Legacy;\nnamespace X\n{\n    using L.Jobs;\n    using System.Threading.Tasks;\n    public static class R { public static object Go() => new Task(); }\n}\n")]
    // Jobs2 also exists at the top, so a walk from the top succeeds - on the wrong namespace.
    [InlineData("shadowed", "namespace Legacy\n{\n    using Jobs;\n    using Jobs2;\n    public static class R { public static object Go() => new Task(); }\n}\n")]
    public void Analyzer_ReportsAClash_ThroughAUsingInsideANamespace(string shape, string source)
    {
        using var repo = MixedRepo(clash: true);
        repo.Write("Legacy/Shadow.cs", "namespace Jobs2 { public class B { } }\nnamespace Legacy.Jobs2 { public class Task { } }\n");
        repo.Write("Legacy/Inner.cs", source);
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var (_, files) = analyzer.UsingsClash(new[] { repo.FullPath("Legacy/Inner.cs") });
        Assert.True(files.Count == 1, $"{shape}: the clash was missed");
    }

    [Theory]
    [InlineData("var t = new Task();", true)]
    [InlineData("var t = new @Task();", true)]         // verbatim: the identifier is still Task
    [InlineData("var t = new Ta­sk();", true)]    // a formatting character inside an identifier is ignored
    [InlineData("var t = new \\u0054ask();", true)]    // a Unicode escape could spell anything: always a maybe
    [InlineData("var t = new \\U00000054ask();", true)]
    [InlineData("var p = @\"C:\\Users\\x\";", false)]  // a path is no escape: \U needs 8 hex digits, \u 4
    [InlineData("var t = new MyTask(); // Tasks", false)]
    [InlineData("", false)]
    public void MayNameAny_MatchesWholeIdentifiers(string code, bool expected)
    {
        var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal) { "Task" };
        Assert.Equal(expected, RoslynCSharpAnalyzer.MayNameAny(Microsoft.CodeAnalysis.Text.SourceText.From(code), names));
    }

    // On a case-sensitive folder Use.cs and use.cs are different files: each is checked on its own, not merged into one.
    [Fact]
    public void Analyzer_ChecksCaseTwinsForAClash_Separately()
    {
        using var repo = new TempRepo();
        repo.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        repo.Write("App/Widget.cs", "namespace App;\npublic sealed class Widget { }\n");
        var legacy = repo.FullPath("Legacy");
        Directory.CreateDirectory(legacy);
        if (!TestFileSystem.TryMakeCaseSensitive(legacy)) return; // this box can't: nothing to test
        repo.Write("Legacy/Legacy.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
            </Project>
            """);
        repo.Write("Legacy/Jobs.cs", "namespace Legacy.Jobs\n{\n    public class Task { public void RunJob() { } }\n}\n");
        repo.Write("Legacy/Use.cs",
            "using System;\nusing Legacy.Jobs;\nnamespace Legacy\n{\n    public static class Runner { public static void Go() { var t = new Task(); t.RunJob(); } }\n}\n");
        repo.Write("Legacy/use.cs", "namespace Legacy\n{\n    public static class Quiet { public static void Go() { } }\n}\n");

        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var (_, files) = analyzer.UsingsClash(new[] { repo.FullPath("Legacy/use.cs"), repo.FullPath("Legacy/Use.cs") });
        Assert.Equal(new[] { repo.FullPath("Legacy/Use.cs") }, files);
    }

    [Fact]
    public void Analyzer_ReportsACodeClash_InTheLegacyFileOnly()
    {
        using var repo = MixedRepo(clash: true);
        var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var candidates = new[] { "Legacy/Use.cs", "Legacy/Jobs.cs", "App/Widget.cs" }.Select(repo.FullPath).ToList();
        var (declarations, files) = analyzer.UsingsClash(candidates);
        Assert.Empty(declarations);
        Assert.Equal(new[] { repo.FullPath("Legacy/Use.cs") }, files);
        // ...and the SDK project still has its implicit usings (List<Widget> binds).
        Assert.Contains(analyzer.FindReferences("Spin"), r => r.RelativePath == "App/Widget.cs" && r.LineText.Contains("w.Spin()"));
    }

    [Fact]
    public void Mcp_CodeClash_BackfillsThatFileByName_AndSaysSo()
    {
        using var repo = MixedRepo(clash: true);
        var r = Mcp(repo, "RunJob");
        Assert.Contains("Legacy/Use.cs:5:", r);           // t.RunJob(), lost by the semantic pass
        Assert.Contains("ImplicitUsings", r);
        Assert.DoesNotContain("App/Broken.cs", r);        // the backfill is that file only, not every .cs file
    }

    [Fact]
    public void Mcp_DeclarationClash_BackfillsEveryCSharpFile()
    {
        using var repo = MixedRepo(clash: true, declarationClash: true);
        var r = Mcp(repo, "RunJob");
        Assert.Contains("Legacy/Caller.cs:3:", r);        // h.Job.RunJob(): Holder.Job's type is the ambiguous Task
        Assert.Contains("ImplicitUsings", r);
    }

    [Fact]
    public void Mcp_MixedRepoWithoutAClash_IsUnchanged()
    {
        using var repo = MixedRepo(clash: false);
        var r = Mcp(repo, "RunJob");
        Assert.Contains("Legacy/Use.cs:5:", r);           // found semantically
        Assert.DoesNotContain("ImplicitUsings", r);
        Assert.DoesNotContain("App/Broken.cs", r);
    }

    [Fact]
    public void Cli_CodeClash_BackfillsThatFileByName_AndSaysSo()
    {
        var cli = TestCli.Find();
        using var repo = MixedRepo(clash: true);
        Run(cli, "index", repo.Root);
        var r = Run(cli, "refs", repo.Root, "RunJob");
        Assert.Contains("Legacy/Use.cs:5:", r.Replace('\\', '/'));
        Assert.Contains("ImplicitUsings", r);
        Assert.DoesNotContain("Broken.cs", r);
    }

    private static string Run(string cli, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(cli)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "CLI did not exit");
        return o.GetAwaiter().GetResult() + "\n" + e.GetAwaiter().GetResult();
    }
}
