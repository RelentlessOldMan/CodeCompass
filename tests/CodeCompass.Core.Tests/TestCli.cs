using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace CodeCompass.Core.Tests;

/// <summary>Locate and run the built <c>CodeCompass.Cli.exe</c> for end-to-end subprocess tests.</summary>
internal static class TestCli
{
    /// <summary>The CLI built in the SAME configuration as this test run, verified to be built from the same code. A
    /// missing or stale exe FAILS the calling test: the old soft-skip let these regression tests pass vacuously on a
    /// fresh clone (the test project doesn't reference the CLI, so `dotnet test` alone never builds it), and "newest
    /// exe anywhere under bin" could silently test an older commit's binary. Build the solution first (check.ps1 does).</summary>
    public static string Find()
    {
        var testDir = AppContext.BaseDirectory;
        var config = testDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";
        var dir = new DirectoryInfo(testDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CodeCompass.Cli"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("can't find the repo root (src/CodeCompass.Cli) above " + testDir);

        var cliDir = Path.Combine(dir.FullName, "src", "CodeCompass.Cli", "bin", config, Path.GetFileName(Path.TrimEndingDirectorySeparator(testDir)));
        var exe = Path.Combine(cliDir, "CodeCompass.Cli.exe");
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
