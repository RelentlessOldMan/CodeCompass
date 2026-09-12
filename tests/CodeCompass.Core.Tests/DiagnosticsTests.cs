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
}
