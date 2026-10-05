using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CodeCompass.Core.Tests;

/// <summary>Locate and run the built <c>CodeCompass.Cli.exe</c> for end-to-end subprocess tests.</summary>
internal static class TestCli
{
    public static string? Find()
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
