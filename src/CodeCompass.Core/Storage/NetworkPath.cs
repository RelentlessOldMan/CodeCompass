namespace CodeCompass.Core.Storage;

/// <summary>
/// Is a path on a network share? A UNC path (<c>\\host\share\...</c>) or a drive letter mapped to a
/// network location (<c>net use</c> / mapped SMB drive, reported as <see cref="DriveType.Network"/>).
/// Used to soften behavior that assumes fast local metadata - e.g. skip the auto-reconcile stat-walk,
/// skip the index pre-scan, warn that the file watcher may miss SMB changes. Root-level check only:
/// a junction/DFS redirect to a network location buried in an otherwise-local tree is not detected
/// (rare); when unsure it errs to "local".
/// </summary>
public static class NetworkPath
{
    public static bool IsNetwork(string fullPath)
    {
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
}
