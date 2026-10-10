using System.Diagnostics;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Is a CodeCompass MCP server serving this root right now? Each serving process holds a marker file,
/// <c>serving/&lt;pid&gt;.alive</c> in the root's cache dir, open with delete-on-close, so the OS removes it when the
/// process exits (a crash included). A marker can still outlive its process - a power loss or a BSOD, or a kill where
/// delete-on-close is only emulated (Unix) - and after a reboot its pid may belong to an unrelated process. So a reader
/// counts a marker only while a process HOLDS it open (an exclusive open of it fails), and deletes one nobody holds. The
/// Grep hook redirects only while one is live: an index on disk with no server means the agent has no search_code to use.
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
        // A few tries: a reader probing a stale marker with this pid's name (see IsHeld) holds it for an instant.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var dir = Path.Combine(IndexStore.GetCacheDir(root), DirName);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, Environment.ProcessId + ".alive");
                // Exclusive: no other process can open it - or, on Windows, delete it - while this one lives. On Unix .NET
                // takes an exclusive flock for it, which readers' probes (IsHeld) see on NFS and CIFS mounts too.
                return new ServerLiveness(new FileStream(path, FileMode.Create, FileAccess.Write,
                                                         FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose));
            }
            catch (IOException) when (attempt < 2) { Thread.Sleep(20); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Does a running process hold a marker for <paramref name="root"/>? Never throws; false on any doubt. Deletes a
    /// marker only when it's provably stale: its process is gone, or nothing holds it.</summary>
    public static bool IsServed(string root)
    {
        try
        {
            var dir = Path.Combine(IndexStore.CacheDirPath(root), DirName);
            if (!Directory.Exists(dir)) return false;
            foreach (var f in Directory.EnumerateFiles(dir, "*.alive"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out var pid)) continue;
                var running = IsRunning(pid);
                var held = running == false ? false : IsHeld(f);
                if (held == true || (held is null && running == true)) return true; // can't probe it: trust the pid
                if (held == false) TryDelete(f);
            }
            return false;
        }
        catch { return false; }
    }

    // Held open by a live server? Its exclusive handle makes another exclusive open fail (on Unix .NET maps this to flock,
    // which the kernel releases with a killed process). One nobody holds opens fine. Null: can't tell.
    private static bool? IsHeld(string marker)
    {
        try
        {
            using var probe = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }        // gone this instant: its server just exited
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }                   // a sharing violation: a server holds it
        catch (UnauthorizedAccessException) { return null; }    // another account's marker, say
    }

    // False only when the pid is certainly not running; null when it can't be asked.
    private static bool? IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; } // no such process
        catch { return null; }                       // no access to ask
    }

    private static void TryDelete(string marker)
    {
        try { File.Delete(marker); } catch { /* best effort: it's ignored either way */ }
    }

    public void Dispose()
    {
        _marker?.Dispose();
        _marker = null;
    }
}
