using System;
using System.Diagnostics;
using System.IO;

namespace CodeCompass.Core.Tests;

internal static class TestFileSystem
{
    /// <summary>Make <paramref name="dir"/> (empty, just created) case-sensitive, so Reg.cs and reg.cs can both exist in it.
    /// Windows 10+ supports it per folder (<c>fsutil file setCaseSensitiveInfo</c>, no admin needed where it works at all);
    /// Linux file systems already are. False if this box can't, after checking that it really took.</summary>
    public static bool TryMakeCaseSensitive(string dir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.Start(new ProcessStartInfo("fsutil.exe", $"file setCaseSensitiveInfo \"{dir}\" enable")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                });
                if (p is null) return false;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(30_000) || p.ExitCode != 0) return false;
            }
            var probe = Path.Combine(dir, "caseprobe");
            File.WriteAllText(probe, "a");
            bool sensitive = !File.Exists(Path.Combine(dir, "CASEPROBE"));
            File.Delete(probe);
            return sensitive;
        }
        catch { return false; }
    }
}
