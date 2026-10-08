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
