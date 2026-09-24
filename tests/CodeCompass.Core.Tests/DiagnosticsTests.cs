using System.IO;
using System.Linq;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Report_And_HealthChecks_Reflect_A_Built_Index()
    {
        using var repo = new TempRepo();
        repo.Write("x.cs", "namespace N { class ZetaSecret { } }");
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
        IndexMetaFile.Write(repo.Root, 1); // as the CLI / MCP server would after a build

        var meta = IndexMetaFile.Read(repo.Root);
        Assert.NotNull(meta);
        Assert.Equal(Path.GetFullPath(repo.Root), Path.GetFullPath(meta!.Root));
        Assert.Equal(BuildInfo.Version, meta.Version);

        var sw = new StringWriter();
        RepoDiagnostics.WriteReport(sw, repo.Root);
        var text = sw.ToString();
        Assert.Contains("CodeCompass diagnostics", text);
        Assert.Contains("== index ==", text);
        Assert.Contains("documents:", text);
        Assert.DoesNotContain("ZetaSecret", text); // must never leak source content into the bundle

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        Assert.Contains(checks, c => c.Name == "index built" && c.Ok);
        Assert.Contains(checks, c => c.Name == "index loads cleanly" && c.Ok);
    }

    [Fact]
    public void HealthChecks_FlagUnbuiltIndex()
    {
        using var repo = new TempRepo();
        repo.Write("x.cs", "class A {}");
        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        Assert.Contains(checks, c => c.Name == "index built" && !c.Ok);
    }

    [Fact]
    public void HealthChecks_Warn_When_CppSources_ButNoCompileDb()
    {
        using var repo = new TempRepo();
        repo.Write("main.c", "int main(void){return 0;}");

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        Assert.Contains(checks, c => c.Name == "C/C++ compile database" && !c.Ok);
    }

    [Fact]
    public void HealthChecks_Ok_When_CompileDbPresent()
    {
        using var repo = new TempRepo();
        repo.Write("main.c", "int main(void){return 0;}");
        repo.Write("compile_commands.json", "[]");

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        Assert.Contains(checks, c => c.Name == "C/C++ compile database" && c.Ok);
    }

    [Fact]
    public void HealthChecks_NoCppCheck_ForPureCSharpRepo()
    {
        using var repo = new TempRepo();
        repo.Write("A.cs", "class A {}");

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        Assert.DoesNotContain(checks, c => c.Name == "C/C++ compile database"); // not relevant -> not shown
    }

    [Fact]
    public void Doctor_Reports_LinkedRoots_And_FlagsUnindexedOne()
    {
        using var project = new TempRepo();
        using var indexedLink = new TempRepo();
        using var unindexedLink = new TempRepo();
        project.Write("a.cs", "class A {}");
        indexedLink.Write("b.cs", "class B {}");
        unindexedLink.Write("c.cs", "class C {}");

        var (t, s, _) = RepositoryIndexer.Build(indexedLink.Root); // only this linked root gets an index
        t.Dispose(); s.Dispose();

        LinkStore.Add(project.Root, indexedLink.Root);
        LinkStore.Add(project.Root, unindexedLink.Root);

        var sw = new StringWriter();
        RepoDiagnostics.WriteReport(sw, project.Root);
        var text = sw.ToString();
        Assert.Contains("== linked roots ==", text);
        Assert.Contains(indexedLink.Root, text);
        Assert.Contains(unindexedLink.Root, text);
        Assert.Contains("indexed: YES", text);

        var checks = RepoDiagnostics.HealthChecks(project.Root);
        Assert.Contains(checks, c => c.Name == $"linked root indexed: {Path.GetFullPath(indexedLink.Root)}" && c.Ok);
        Assert.Contains(checks, c => c.Name == $"linked root indexed: {Path.GetFullPath(unindexedLink.Root)}" && !c.Ok);
    }
}
