using System.Diagnostics;

namespace CodeCompass.Core.Diagnostics;

/// <summary>
/// Process-level diagnostics bootstrap: install once at startup from each entry point (the MCP server and
/// the CLI). It (1) routes otherwise-unhandled exceptions to the log so a background crash isn't silent,
/// and (2) for long-running roles, drops a liveness marker cleared on normal exit - so the NEXT launch can
/// report that a previous session died without shutting down cleanly. Everything here is best-effort and
/// never throws: diagnostics must not be able to break the app it is diagnosing.
/// </summary>
public static class DiagnosticsSession
{
    private static int _started;
    private static string? _markerPath;

    /// <param name="role">A short tag for this process kind, e.g. "mcp" or "cli".</param>
    /// <param name="trackSession">Write a liveness marker (and detect prior abnormal exits). Worth it for a
    /// long-running server; skipped for short-lived CLI commands, which would only churn marker files.</param>
    public static void Start(string role, bool trackSession)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return; // idempotent - only the first call wins

        // (2) Top-level nets. These fire for exceptions no local try/catch handled - a fatal fault on a
        // background thread, or a faulted Task nobody awaited. Log before the process unwinds/terminates.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Log.Global.Error(
                    $"UNHANDLED exception ({role}, session {Log.SessionId}, terminating={e.IsTerminating})",
                    e.ExceptionObject as Exception);
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Log.Global.Error($"UNOBSERVED task exception ({role}, session {Log.SessionId})", e.Exception);
                e.SetObserved(); // we've recorded it; don't let it escalate to process termination
            }
            catch { }
        };

        if (trackSession)
        {
            try { _ = ReportAbnormalPriorSessions(); } catch { }
            try { WriteMarker(role); } catch { }
            // Cleared on a NORMAL exit; a hard crash / kill leaves it behind for the next start to report.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ClearMarker();
        }
    }

    private static string MarkerDir() => Path.Combine(Log.Directory, "sessions");

    private static void WriteMarker(string role)
    {
        var dir = MarkerDir();
        System.IO.Directory.CreateDirectory(dir);
        var proc = Process.GetCurrentProcess();
        _markerPath = Path.Combine(dir, $"{Log.SessionId}.active");
        // pid | process-start-ticks (UTC) | role | sessionId  - start-ticks guards against PID reuse.
        File.WriteAllText(_markerPath,
            $"{proc.Id}|{proc.StartTime.ToUniversalTime().Ticks}|{role}|{Log.SessionId}");
    }

    private static void ClearMarker()
    {
        var p = _markerPath;
        if (p is null) return;
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    // Any marker whose owning process is no longer alive ended without clearing it -> abnormal exit. Report
    // it to the shared log (which the support bundle includes) and remove the stale marker. Markers for
    // still-running sibling processes are left untouched. Returns the number of abnormal sessions found
    // (internal for tests). Best-effort throughout.
    internal static int ReportAbnormalPriorSessions()
    {
        int abnormal = 0;
        var dir = MarkerDir();
        if (!System.IO.Directory.Exists(dir)) return 0;

        foreach (var f in System.IO.Directory.GetFiles(dir, "*.active"))
        {
            try
            {
                var parts = File.ReadAllText(f).Split('|');
                int pid = int.Parse(parts[0]);
                long startTicks = long.Parse(parts[1]);
                if (IsAlive(pid, startTicks)) continue; // a concurrent, still-running session - leave it

                var role = parts.Length > 2 ? parts[2] : "?";
                var sid = parts.Length > 3 ? parts[3] : Path.GetFileNameWithoutExtension(f);
                var started = new DateTime(startTicks, DateTimeKind.Utc).ToLocalTime();
                Log.Global.Warn(
                    $"previous session {sid} ({role}, pid {pid}, started {started:yyyy-MM-dd HH:mm:ss}) " +
                    "did NOT shut down cleanly (no normal-exit marker removal) - it may have crashed or been killed");
                abnormal++;
                TryDelete(f);
            }
            catch { TryDelete(f); } // unparseable / unreadable marker: drop it so it isn't reported forever
        }
        return abnormal;
    }

    // A pid is "alive" only if a process with that id exists AND started at the recorded time (a reused pid
    // belongs to a different, later-started process). Unknowable start time -> assume alive (don't over-report).
    private static bool IsAlive(int pid, long startTicks)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try
            {
                long actual = p.StartTime.ToUniversalTime().Ticks;
                return Math.Abs(actual - startTicks) <= TimeSpan.FromSeconds(2).Ticks;
            }
            catch { return true; } // can't read start time (access denied) - err on the side of "alive"
        }
        catch { return false; } // no process with that id
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
