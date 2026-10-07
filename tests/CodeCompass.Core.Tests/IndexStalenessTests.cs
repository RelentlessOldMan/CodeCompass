using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// #4 (field report carried item): a version-mismatched index was served SILENTLY - provenance showed only in
// doctor/link list, never checked at query time. Now the ACTIONABLE staleness (the index's OUTPUT logic is
// behind the running binary, judged on CONTENT version not the git-derived product version) is disclosed by the
// query tools themselves. Keying on content version keeps it silent across ordinary upgrades (no cry-wolf).
public class IndexStalenessTests
{
    [Fact]
    public void BehindNote_FiresOnlyWhenContentVersionBehind()
    {
        // ContentVersion 0 < current (BuildInfo.IndexerContentVersion >= 1) -> behind -> note fires and is actionable.
        var stale = new IndexMeta("C:\\repo", "9.9.9", DateTime.UtcNow.ToString("o"), 10, ContentVersion: 0);
        var note = IndexMetaFile.BehindNote(stale, "C:\\repo");
        Assert.NotEqual("", note);
        Assert.Contains("older indexer", note);
        Assert.Contains("codecompass index", note);                 // tells the user how to fix it

        // Current content version -> NOT behind -> silent (even though the product version string differs wildly).
        var current = stale with { ContentVersion = BuildInfo.IndexerContentVersion };
        Assert.Equal("", IndexMetaFile.BehindNote(current, "C:\\repo"));

        // Unknown meta -> silent (don't cry wolf on a pre-stamp index we can't judge).
        Assert.Equal("", IndexMetaFile.BehindNote(null, "C:\\repo"));
    }

    // End-to-end: a query over an index whose meta says it was built by an older indexer must DISCLOSE it - on
    // STDOUT, with the answer, so a `def ... > out.txt` keeps it (the co-worker's v2/v3 finding: on stderr, a redirected
    // answer lost the warning). Builds the index, then rewrites meta.json's content version to a stale value (the public
    // Write API always stamps the current one, so we edit the record directly) and runs `def`.
    [Fact]
    public void Cli_Query_StaleIndex_DisclosesRebuildNeeded()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class C { void M() { } } }\n");
        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));

        // Rewrite the meta to look built by an older indexer (content v0 < current).
        var cacheDir = IndexStore.GetCacheDir(repo.Root);
        var metaPath = Path.Combine(cacheDir, "meta.json");
        var meta = JsonSerializer.Deserialize<IndexMeta>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta with { ContentVersion = 0 }));

        Assert.Equal(0, RunCli(cli, "def", repo.Root, out var stdout, out _, "C"));
        Assert.Contains("older indexer", stdout);                   // staleness disclosed at query time, with the answer
        Assert.Contains("codecompass index", stdout);
    }

    // CLI queries search the project root only; the MCP server federates linked roots. A CLI zero must say the linked
    // roots weren't searched (field report: a confident empty answer read as "not found anywhere") - on STDOUT, so a
    // redirected answer keeps it.
    [Fact]
    public void Cli_Query_WithLinkedRoots_SaysTheyWereNotSearched()
    {
        var cli = TestCli.Find();

        using var project = new TempRepo();
        project.Write("app.c", "int app(void){ return 0; }\n");
        using var lib = new TempRepo();
        lib.Write("lib.c", "int only_in_lib_zq(void){ return 1; }\n");
        Assert.Equal(0, RunCli(cli, "index", project.Root, out _, out _));
        LinkStore.Add(project.Root, lib.Root);
        try
        {
            foreach (var cmd in new[] { "search", "def", "symbols", "refs" })
            {
                RunCli(cli, cmd, project.Root, out var stdout, out _, "only_in_lib_zq");
                Assert.Contains("1 linked root(s) are NOT searched", stdout);
            }
        }
        finally { LinkStore.Remove(project.Root, lib.Root); }
    }

    [Fact]
    public void Cli_Query_FreshIndex_NoStalenessNote()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class C { void M() { } } }\n");
        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "def", repo.Root, out var stdout, out var stderr, "C"));
        var all = stdout + "\n" + stderr;
        Assert.DoesNotContain("older indexer", all);                // a current index must stay silent
    }

    // The MCP twin (field report v4 left this "untested": its probe touched a file's mtime, which is not what this
    // feature detects). An index built by an older INDEXER must be disclosed on the tool result itself.
    [Fact]
    public void Mcp_Query_StaleIndexerVersion_DisclosesRebuildNeeded()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class StaleProbeZq { void M() { } } }\n");
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            var metaPath = Path.Combine(IndexStore.GetCacheDir(repo.Root), "meta.json");
            var meta = JsonSerializer.Deserialize<IndexMeta>(File.ReadAllText(metaPath))!;
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta with { ContentVersion = BuildInfo.IndexerContentVersion - 1 }));

            var r = CodeCompass.Mcp.CodeCompassTools.FindDefinition("StaleProbeZq");
            Assert.Contains("StaleProbeZq", r);                          // still answers...
            Assert.Contains("older indexer", r);                         // ...and says the index needs a rebuild
            Assert.Contains("codecompass index", r);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    private static int RunCli(string exe, string cmd, string repo, out string stdout, out string stderr, string? arg = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(cmd);
        psi.ArgumentList.Add(repo);
        if (arg is not null) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(120_000);
        stdout = o.GetAwaiter().GetResult();
        stderr = e.GetAwaiter().GetResult();
        return p.ExitCode;
    }

}
