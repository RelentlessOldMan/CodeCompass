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
    public void Report_RedactsSecretLookingEnvValues_ButKeepsNamesAndBenignValues()
    {
        // The support bundle is meant to be shared, so a CODECOMPASS_* var whose NAME hints at a secret
        // (TOKEN/SECRET/KEY/PASS/PWD/CRED) must have its VALUE masked; benign knobs still print verbatim.
        // Regression guard for the diagnostics privacy guarantee - a leak here ships credentials to whoever
        // receives the bundle.
        const string secretVar = "CODECOMPASS_FUZZTEST_APIKEY";               // matches "KEY"
        const string secretVal = "sk-supersecret-DO-NOT-LEAK-9f83a2b1";
        const string benignVar = "CODECOMPASS_FUZZTEST_MAX_MB";               // no secret marker
        const string benignVal = "12345";
        var prevSecret = System.Environment.GetEnvironmentVariable(secretVar);
        var prevBenign = System.Environment.GetEnvironmentVariable(benignVar);
        try
        {
            System.Environment.SetEnvironmentVariable(secretVar, secretVal);
            System.Environment.SetEnvironmentVariable(benignVar, benignVal);

            using var repo = new TempRepo();
            repo.Write("x.cs", "namespace N { class A { } }");
            var (t, s, _) = RepositoryIndexer.Build(repo.Root);
            t.Dispose(); s.Dispose();

            var sw = new StringWriter();
            RepoDiagnostics.WriteReport(sw, repo.Root);
            var text = sw.ToString();

            Assert.DoesNotContain(secretVal, text);            // the secret value must never appear
            Assert.Contains($"{secretVar}=<redacted>", text);  // masked, but the name is still disclosed
            Assert.Contains($"{benignVar}={benignVal}", text); // a non-secret knob prints its value verbatim
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(secretVar, prevSecret);
            System.Environment.SetEnvironmentVariable(benignVar, prevBenign);
        }
    }

    // Direct pin on the prefix-vs-tail boundary the redaction relies on: the product name itself contains
    // "PASS" (codecomPASS), so the secret markers must be matched only AFTER the CODECOMPASS_ prefix -
    // otherwise every CODECOMPASS_* var is masked (gutting the bundle) or none is (leaking credentials). The
    // full-report test covers this only indirectly with a single var; this is far tighter and cheaper.
    [Theory]
    [InlineData("CODECOMPASS_API_TOKEN", true)]
    [InlineData("CODECOMPASS_GH_SECRET", true)]
    [InlineData("CODECOMPASS_SIGNING_KEY", true)]
    [InlineData("CODECOMPASS_DB_PASSWORD", true)]   // contains PASS in the tail
    [InlineData("CODECOMPASS_SMTP_PWD", true)]
    [InlineData("CODECOMPASS_AZURE_CRED", true)]
    [InlineData("CODECOMPASS_MAX_MB", false)]       // benign knob
    [InlineData("CODECOMPASS_LOG_DIR", false)]
    [InlineData("CODECOMPASS_", false)]             // bare prefix: empty tail must NOT trip on "PASS" in the prefix
    [InlineData("CODECOMPASS_FORCE_NETWORK", false)]
    public void LooksSecret_MatchesMarkersInTailOnly(string name, bool expected)
        => Assert.Equal(expected, RepoDiagnostics.LooksSecret(name));

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
    public void HealthChecks_WalkCoverage_OkWhenNoneDropped_WarnsWhenDropped()
    {
        using var repo = new TempRepo();
        repo.Write("x.cs", "class A {}");
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();

        IndexMetaFile.Write(repo.Root, 1); // droppedDirs defaults to 0
        Assert.Contains(RepoDiagnostics.HealthChecks(repo.Root), c => c.Name == "walk coverage complete" && c.Ok);

        IndexMetaFile.Write(repo.Root, 1, droppedDirs: 2); // simulate a build that gave up on 2 dirs
        var warned = RepoDiagnostics.HealthChecks(repo.Root).Single(c => c.Name == "walk coverage complete");
        Assert.False(warned.Ok);
        Assert.Contains("2", warned.Detail);
    }

    [Fact]
    public void HealthChecks_FlagUnbuiltIndex()
    {
        using var repo = new TempRepo();
        repo.Write("x.cs", "class A {}");
        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        Assert.Contains(checks, c => c.Name == "index built" && !c.Ok);
    }

    // C/C++ references are a name search (no compiler), so doctor no longer warns about compile databases or missing
    // headers: neither affects any answer. A C/C++ repo with a missing header gets no C/C++ check at all.
    [Fact]
    public void HealthChecks_NoCompilerChecks_ForCppRepo()
    {
        using var repo = new TempRepo();
        repo.Write("dev.c", "#include \"VENDOR_missing.h\"\nint dev(void){return 0;}");

        var checks = RepoDiagnostics.HealthChecks(repo.Root);
        Assert.DoesNotContain(checks, c => c.Name.StartsWith("C/C++"));
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
