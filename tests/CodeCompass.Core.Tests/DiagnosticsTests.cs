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
    public void HealthChecks_FreshIndex_IsUpToDate_AndProvenanceIsNotAWarning()
    {
        using var repo = new TempRepo();
        repo.Write("x.cs", "class A {}");
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
        IndexMetaFile.Write(repo.Root, 1);

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        // A just-built index is not behind the current indexer.
        Assert.Contains(checks, c => c.Name == "indexer up to date" && c.Ok);
        // Provenance is informational, never a warning (product version bumps every commit; not a fault).
        Assert.Contains(checks, c => c.Name == "index provenance" && c.Ok);
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
    public void HealthChecks_Ok_When_ConfiguredCompileDbPresent()
    {
        using var repo = new TempRepo();
        repo.Write("main.c", "int main(void){return 0;}");
        repo.Write("out/compile_commands.json", "[]");                 // nonstandard location
        repo.Write(".codecompass.json", """{ "compileCommands": ["out"] }""");

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        // No warning: the configured location is honored, so doctor sees a compile DB.
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
    public void HealthChecks_Flag_UnresolvableInclude_AndNameIt()
    {
        using var repo = new TempRepo();
        // Two TUs: one includes a header that lives nowhere in the tree (a vendor/system header), one is clean.
        repo.Write("dev.c", "#include \"VENDOR_missing.h\"\nint dev(void){return 0;}");
        repo.Write("ok.c", "#include \"local.h\"\nint ok(void){return 1;}");
        repo.Write("local.h", "int ok(void);");

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        var scan = checks.Single(c => c.Name == "C/C++ includes resolvable");
        Assert.False(scan.Ok);
        Assert.Contains("1 of 2", scan.Detail);
        Assert.Contains("VENDOR_missing.h", scan.Detail);
    }

    [Fact]
    public void HealthChecks_StdAndTreeIncludes_DoNotFalseFlag()
    {
        using var repo = new TempRepo();
        // Angle stdlib (stdio.h), extensionless C++ header (<vector>), and a quote include resolved in-tree.
        repo.Write("main.c", "#include <stdio.h>\n#include <vector>\n#include \"util.h\"\nint main(void){return 0;}");
        repo.Write("util.h", "void util(void);");

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        var scan = checks.Single(c => c.Name == "C/C++ includes resolvable");
        Assert.True(scan.Ok, scan.Detail);
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
