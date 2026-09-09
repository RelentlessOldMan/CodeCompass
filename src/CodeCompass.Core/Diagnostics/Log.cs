using System.Diagnostics;
using System.Text;
using CodeCompass.Core.Storage;

namespace CodeCompass.Core.Diagnostics;

public enum LogLevel { Off = 0, Error = 1, Warn = 2, Info = 3, Debug = 4 }

/// <summary>
/// Central, self-limiting disk log for debugging in the field. Two views live under one
/// folder (%LOCALAPPDATA%\CodeCompass\logs): a shared <c>codecompass.log</c> that carries
/// process lifecycle plus every warning/error from any repo, and per-repo files
/// (<c>repo-&lt;name&gt;-&lt;key&gt;.log</c>) with that repo's full detail. Each file is size-capped
/// and rotated (default 5 MB x 4 files), so logging can never grow unbounded. Logging never
/// throws and never blocks real work: all failures are swallowed.
/// </summary>
public static class Log
{
    /// <summary>Process-wide log: lifecycle events and all warnings/errors across repos.</summary>
    public static DiskLogger Global { get; } = BuildGlobal();

    private static readonly object ReposGate = new();
    private static readonly Dictionary<string, DiskLogger> Repos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-repo log for <paramref name="repoRoot"/>. Full detail lands in the repo file;
    /// warnings and errors are also mirrored to <see cref="Global"/> so the common log is a
    /// single place to spot trouble.
    /// </summary>
    public static DiskLogger For(string repoRoot)
    {
        var full = Path.GetFullPath(repoRoot);
        lock (ReposGate)
        {
            if (Repos.TryGetValue(full, out var existing)) return existing;

            string tag;
            try { tag = SafeName(new DirectoryInfo(full).Name) + "-" + IndexStore.RepoKey(full); }
            catch { tag = "repo"; }
            var logger = new DiskLogger(Path.Combine(LogDir(), $"repo-{tag}.log"),
                                        component: $"repo:{SafeName(SafeDirName(full))}",
                                        mirror: Global);
            Repos[full] = logger;
            return logger;
        }
    }

    /// <summary>The folder where all CodeCompass logs are written.</summary>
    public static string Directory => LogDir();

    internal static string LogDir()
    {
        var dir = Environment.GetEnvironmentVariable("CODECOMPASS_LOG_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(IndexStore.BaseDir(), "logs");
        return dir;
    }

    internal static LogLevel Level()
    {
        var raw = Environment.GetEnvironmentVariable("CODECOMPASS_LOG");
        if (raw is "0" || string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)) return LogLevel.Off;

        var lvl = Environment.GetEnvironmentVariable("CODECOMPASS_LOG_LEVEL");
        return lvl?.Trim().ToLowerInvariant() switch
        {
            "off" or "none" => LogLevel.Off,
            "error" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warn,
            "debug" or "trace" or "verbose" => LogLevel.Debug,
            "info" => LogLevel.Info,
            _ => LogLevel.Info, // default
        };
    }

    private static DiskLogger BuildGlobal() =>
        new(Path.Combine(LogDir(), "codecompass.log"), component: "codecompass", mirror: null);

    private static string SafeDirName(string full)
    {
        try { return new DirectoryInfo(full).Name; } catch { return "repo"; }
    }

    private static string SafeName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var r = sb.ToString().Trim('.', '_');
        return r.Length == 0 ? "repo" : (r.Length > 40 ? r[..40] : r);
    }
}

/// <summary>
/// A single rotating log file. Appends are process- and thread-safe (shared-write handle
/// plus a named mutex for the rotation step). All I/O failures are swallowed so a logging
/// problem never surfaces as a tool failure.
/// </summary>
public sealed class DiskLogger
{
    private readonly string _path;
    private readonly string _component;
    private readonly DiskLogger? _mirror;
    private readonly object _gate = new();
    private readonly int _pid;

    public DiskLogger(string path, string component, DiskLogger? mirror)
    {
        _path = path;
        _component = component;
        _mirror = mirror;
        try { _pid = Environment.ProcessId; } catch { _pid = 0; }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message, null);
    public void Info(string message) => Write(LogLevel.Info, message, null);
    public void Warn(string message) => Write(LogLevel.Warn, message, null);
    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    private static long MaxBytes()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_LOG_MAX_MB");
        long mb = long.TryParse(env, out var v) && v > 0 ? v : 5;
        return mb * 1024L * 1024;
    }

    private static int Keep()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_LOG_KEEP");
        return int.TryParse(env, out var v) && v >= 0 ? v : 3;
    }

    private void Write(LogLevel level, string message, Exception? ex)
    {
        if (level > Log.Level()) return;
        try
        {
            var sb = new StringBuilder(160);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(' ').Append(Pad(level))
              .Append(" [").Append(_pid).Append("] [").Append(_component).Append("] ")
              .Append(message);
            if (ex is not null)
                sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message)
                  .Append(Environment.NewLine).Append(ex.StackTrace);
            sb.Append(Environment.NewLine);
            var line = sb.ToString();

            AppendWithRotation(line);
            // Problems (warn/error) bubble up to the common log verbatim; routine info/debug
            // stays in the per-repo file so the common log remains a lean "what went wrong" view.
            if (_mirror is not null && level <= LogLevel.Warn)
                _mirror.AppendWithRotation(line);
        }
        catch { /* logging must never throw */ }
    }

    private void AppendWithRotation(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfNeeded();
                var bytes = Encoding.UTF8.GetBytes(line);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        fs.Write(bytes, 0, bytes.Length);
                        return;
                    }
                    catch (IOException) when (attempt < 3)
                    {
                        Thread.Sleep(5); // another process holds the handle briefly; retry
                    }
                }
            }
        }
        catch { /* swallow */ }
    }

    // Caller holds _gate. Size-capped cascade: .log -> .log.1 -> ... -> .log.N (oldest dropped).
    // A named mutex serializes the rename across processes; failures are non-fatal (we just
    // keep writing to the current file, which may briefly exceed the cap).
    private void RotateIfNeeded()
    {
        long max = MaxBytes();
        var fi = new FileInfo(_path);
        if (!fi.Exists || fi.Length < max) return;

        var mutexName = "Global\\CodeCompass_log_" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(_path.ToLowerInvariant())))[..16];

        Mutex? mutex = null;
        bool held = false;
        try
        {
            mutex = new Mutex(false, mutexName);
            try { held = mutex.WaitOne(200); } catch (AbandonedMutexException) { held = true; }

            fi.Refresh();
            if (!fi.Exists || fi.Length < max) return; // another process already rotated

            int keep = Keep();
            if (keep == 0) { AtomicFile.TryDelete(_path); return; }

            AtomicFile.TryDelete($"{_path}.{keep}");
            for (int i = keep - 1; i >= 1; i--)
                TryMove($"{_path}.{i}", $"{_path}.{i + 1}");
            TryMove(_path, $"{_path}.1");
        }
        catch { /* rotation is best-effort */ }
        finally
        {
            if (held) { try { mutex!.ReleaseMutex(); } catch { } }
            mutex?.Dispose();
        }
    }

    private static void TryMove(string from, string to)
    {
        try { if (File.Exists(from)) { AtomicFile.TryDelete(to); File.Move(from, to); } } catch { }
    }

    private static string Pad(LogLevel l) => l switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN ",
        LogLevel.Info => "INFO ",
        LogLevel.Debug => "DEBUG",
        _ => "     ",
    };
}
