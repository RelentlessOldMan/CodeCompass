using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace CodeCompass.Core.Tests;

internal static class TestEnvironment
{
    // Redirect the cache + log dirs to a per-run temp dir BEFORE any test runs, so the suite never writes
    // into the real %LOCALAPPDATA%\CodeCompass. Without this, every TempRepo build left an index cache dir
    // and a repo-*.log behind (thousands had accumulated on dev boxes). Best-effort cleanup on process exit.
    [ModuleInitializer]
    public static void Init()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "cc-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODECOMPASS_CACHE_DIR", Path.Combine(baseDir, "cache"));
        Environment.SetEnvironmentVariable("CODECOMPASS_LOG_DIR", Path.Combine(baseDir, "logs"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best effort */ }
        };
    }
}
