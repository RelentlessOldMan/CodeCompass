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

    // End-to-end: a query over an index whose meta says it was built by an older indexer must DISCLOSE it on
    // stderr. Builds the index, then rewrites meta.json's content version to a stale value (the public Write API
    // always stamps the current one, so we edit the record directly) and runs `def`. Skips softly without the CLI.
    [Fact]
    public void Cli_Query_StaleIndex_DisclosesRebuildNeeded()
    {
        var cli = FindCliExe();
        if (cli is null) return;

        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class C { void M() { } } }\n");
        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));

        // Rewrite the meta to look built by an older indexer (content v0 < current).
        var cacheDir = IndexStore.GetCacheDir(repo.Root);
        var metaPath = Path.Combine(cacheDir, "meta.json");
        var meta = JsonSerializer.Deserialize<IndexMeta>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta with { ContentVersion = 0 }));

        Assert.Equal(0, RunCli(cli, "def", repo.Root, out var stdout, out var stderr, "C"));
        var all = stdout + "\n" + stderr;
        Assert.Contains("older indexer", all);                      // staleness disclosed at query time
        Assert.Contains("codecompass index", all);
    }

    [Fact]
    public void Cli_Query_FreshIndex_NoStalenessNote()
    {
        var cli = FindCliExe();
        if (cli is null) return;

        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class C { void M() { } } }\n");
        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "def", repo.Root, out var stdout, out var stderr, "C"));
        var all = stdout + "\n" + stderr;
        Assert.DoesNotContain("older indexer", all);                // a current index must stay silent
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

    private static string? FindCliExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var cliBin = Path.Combine(dir.FullName, "src", "CodeCompass.Cli", "bin");
            if (Directory.Exists(cliBin))
                return Directory.EnumerateFiles(cliBin, "CodeCompass.Cli.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }
}
