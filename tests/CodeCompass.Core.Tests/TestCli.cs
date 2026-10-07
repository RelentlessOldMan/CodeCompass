using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace CodeCompass.Core.Tests;

/// <summary>Locate and run the built <c>CodeCompass.Cli.exe</c> (or the MCP server) for end-to-end subprocess tests.</summary>
internal static class TestCli
{
    /// <summary>The CLI built in the SAME configuration as this test run, verified to be built from the same code. A
    /// missing or stale exe FAILS the calling test: the old soft-skip let these regression tests pass vacuously on a
    /// fresh clone (the test project doesn't reference the CLI, so `dotnet test` alone never builds it), and "newest
    /// exe anywhere under bin" could silently test an older commit's binary. Build the solution first (check.ps1 does).</summary>
    public static string Find() => Find("CodeCompass.Cli");

    /// <summary>The MCP server, held to the same rule as <see cref="Find()"/>: the stdio tests are the only ones that drive
    /// the real server, and a soft-skip or another configuration's binary would let them pass without testing this code.</summary>
    public static string FindMcp() => Find("CodeCompass.Mcp");

    private static string Find(string project)
    {
        var testDir = AppContext.BaseDirectory;
        var config = testDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";
        var dir = new DirectoryInfo(testDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", project))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException($"can't find the repo root (src/{project}) above " + testDir);

        var cliDir = Path.Combine(dir.FullName, "src", project, "bin", config, Path.GetFileName(Path.TrimEndingDirectorySeparator(testDir)));
        var exe = Path.Combine(cliDir, project + ".exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException($"{exe} is not built - run `dotnet build CodeCompass.sln -c {config}` (check.ps1 does) so the subprocess tests exercise this commit.");
        foreach (var lib in new[] { "CodeCompass.Core.dll", "CodeCompass.Semantics.dll" })
            if (!SameBytes(Path.Combine(cliDir, lib), Path.Combine(testDir, lib)))
                throw new InvalidOperationException($"{exe} is STALE ({lib} differs from this test build) - rebuild the solution: `dotnet build CodeCompass.sln -c {config}`.");
        return exe;
    }

    private static bool SameBytes(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b)) return false;
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    /// <summary>Run the CLI to completion and return its exit code and its two streams SEPARATELY - qualifiers on an answer
    /// belong on stdout (a redirected answer must keep them), and a test can only check that if it doesn't merge them.</summary>
    public static (int Exit, string Stdout, string Stderr) Run(string exe, params string[] args)
    {
        using var p = Start(exe, args);
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(entireProcessTree: true); } catch { } throw new TimeoutException("CLI did not exit"); }
        p.WaitForExit();
        return (p.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult());
    }

    /// <summary>Start the CLI with the given arguments, stdout/stderr redirected. Caller owns the process.</summary>
    public static Process Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!; // inherits CODECOMPASS_CACHE_DIR (TestEnvironment), so it shares this host's caches
    }
}
