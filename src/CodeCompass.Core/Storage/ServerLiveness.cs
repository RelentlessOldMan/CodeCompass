using System.Diagnostics;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Is a CodeCompass MCP server serving this root right now? Each serving process holds a marker file,
/// <c>serving/&lt;pid&gt;.alive</c> in the root's cache dir, open with delete-on-close, so the OS removes it when the
/// process exits (a crash included). A reader also checks the pid is running, so a marker that outlives its process
/// (a filesystem without delete-on-close semantics) never counts. The Grep hook redirects only while one is live: an
/// index on disk with no server means the agent has no search_code to use.
/// Limitation: this is per ROOT, not per agent session - if another session (or Codex) serves the same repo, a session
/// whose own server failed to connect is still redirected. The hook can't tell which server belongs to its session.
/// </summary>
public sealed class ServerLiveness : IDisposable
{
    private const string DirName = "serving";
    private FileStream? _marker;

    private ServerLiveness(FileStream marker) => _marker = marker;

    /// <summary>Mark this process as serving <paramref name="root"/> until disposed. Null if the marker can't be created
    /// (an unwritable cache dir): the hook then leaves Grep alone, the safe side.</summary>
    public static ServerLiveness? Mark(string root)
    {
        try
        {
            var dir = Path.Combine(IndexStore.GetCacheDir(root), DirName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Environment.ProcessId + ".alive");
            return new ServerLiveness(new FileStream(path, FileMode.Create, FileAccess.Write,
                                                     FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.DeleteOnClose));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Does a running process hold a marker for <paramref name="root"/>? Never throws; false on any doubt.</summary>
    public static bool IsServed(string root)
    {
        try
        {
            var dir = Path.Combine(IndexStore.CacheDirPath(root), DirName);
            if (!Directory.Exists(dir)) return false;
            foreach (var f in Directory.EnumerateFiles(dir, "*.alive"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), out var pid) && IsRunning(pid)) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; } // no such process, or no access to ask
    }

    public void Dispose()
    {
        _marker?.Dispose();
        _marker = null;
    }
}
