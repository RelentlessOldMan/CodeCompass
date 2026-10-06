namespace CodeCompass.Core.Storage;

/// <summary>
/// Is a path on a network share? A UNC path (<c>\\host\share\...</c>) or a drive letter mapped to a
/// network location (<c>net use</c> / mapped SMB drive, reported as <see cref="DriveType.Network"/>).
/// Used to soften behavior that assumes fast local metadata - e.g. skip the auto-reconcile stat-walk,
/// skip the index pre-scan, warn that the file watcher may miss SMB changes. On Linux/macOS a share is a
/// mount point, so the mount table decides (NFS/CIFS/SMB mounts report <see cref="DriveType.Network"/>).
/// A Windows junction/symlink to a network location buried in an otherwise-local tree is not detected
/// (rare; DFS namespaces are UNC paths and are); when unsure it errs to "local".
///
/// Override: <c>CODECOMPASS_FORCE_NETWORK=1/0</c> forces the answer regardless of detection. It's the
/// intended escape hatch when the root check can't see a network location (a DFS/junction redirect),
/// and it lets tests exercise every network-gated path on a local directory (fake-out, no real share).
/// </summary>
public static class NetworkPath
{
    public static bool IsNetwork(string fullPath)
    {
        var forced = Forced();
        if (forced is not null) return forced.Value;
        if (string.IsNullOrEmpty(fullPath)) return false;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return true; // UNC
        if (!OperatingSystem.IsWindows()) return OnNetworkMount(fullPath, UnixMountRoots(), IsNetworkMount);
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':')
                return new DriveInfo(root).DriveType == DriveType.Network; // mapped network drive
        }
        catch { /* unknown -> treat as local */ }
        return false;
    }

    /// <summary>Linux/macOS: is the deepest mount containing <paramref name="fullPath"/> a network filesystem (NFS, CIFS,
    /// SMB...)? There, a network share is just a mount point, not a path shape.
    /// The containing mount is picked from the mount LIST alone; only that one mount is asked for its type
    /// (<paramref name="isNetworkRoot"/>), because asking is a statfs, which blocks on a hung hard-mounted NFS export or
    /// wakes an automount - so an unrelated mount must never be touched.</summary>
    internal static bool OnNetworkMount(string fullPath, IReadOnlyList<string> mountRoots, Func<string, bool> isNetworkRoot)
    {
        string? best = null; int bestLen = -1;
        foreach (var root in mountRoots)
        {
            var r = root.TrimEnd('/');
            bool under = r.Length == 0 || fullPath == r || fullPath.StartsWith(r + "/", StringComparison.Ordinal);
            if (under && r.Length > bestLen) { bestLen = r.Length; best = root; }
        }
        return best is not null && isNetworkRoot(best);
    }

    // The mount list (names only - no per-mount statfs), re-read at most every 30 s, and each asked mount's type, cached:
    // IsNetwork runs per re-read file during an update.
    private static string[]? _mountRoots;
    private static long _mountRootsAt;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _mountIsNetwork = new(StringComparer.Ordinal);

    private static string[] UnixMountRoots()
    {
        long now = Environment.TickCount64;
        var m = _mountRoots;
        if (m is not null && now - Volatile.Read(ref _mountRootsAt) < 30_000) return m;
        var list = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { list.Add(d.Name); } catch { /* skip */ }
            }
        }
        catch { /* unknown -> local */ }
        m = list.ToArray();
        _mountRoots = m;
        _mountIsNetwork.Clear(); // the table may have changed (a remount)
        Volatile.Write(ref _mountRootsAt, now);
        return m;
    }

    private static bool IsNetworkMount(string root) => _mountIsNetwork.GetOrAdd(root, r =>
    {
        try { return new DriveInfo(r).DriveType == DriveType.Network; } catch { return false; }
    });

    // CODECOMPASS_FORCE_NETWORK: true/1 -> always network, false/0 -> never; unset/other -> detect.
    private static bool? Forced()
    {
        var v = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        if (v is null) return null;
        if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }
}
