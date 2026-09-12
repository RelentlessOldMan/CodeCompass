namespace CodeCompass.Core.Storage;

/// <summary>
/// Is a path on a network share? A UNC path (<c>\\host\share\...</c>) or a drive letter mapped to a
/// network location (<c>net use</c> / mapped SMB drive, reported as <see cref="DriveType.Network"/>).
/// Used to soften behavior that assumes fast local metadata - e.g. skip the auto-reconcile stat-walk,
/// skip the index pre-scan, warn that the file watcher may miss SMB changes. Root-level check only:
/// a junction/DFS redirect to a network location buried in an otherwise-local tree is not detected
/// (rare); when unsure it errs to "local".
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
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':')
                return new DriveInfo(root).DriveType == DriveType.Network; // mapped network drive
        }
        catch { /* unknown -> treat as local */ }
        return false;
    }

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
