namespace CodeCompass.Core.Storage;

/// <summary>
/// Which of a batch of paths are folders, at a cost that doesn't grow with the batch: several paths in one parent take one
/// listing of that parent, not a stat each. A build's clean (or an in-tree build) reports thousands of .o/.obj paths named
/// like an ignored file type, any of which might be a folder named like one (src/Contoso.Lib) - a stat each, under the
/// index write lock and on a network root a round trip each, stalled every query behind it.
/// </summary>
public static class FolderProbe
{
    // A parent holding at least this many of the paths is listed once; fewer are stat'ed (a listing reads every entry).
    private const int ListAtLeast = 8;

    private static readonly StringComparer NameComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>File-system calls made on this thread (tests).</summary>
    [ThreadStatic] internal static int DiskCallsForTest;

    /// <summary>The paths (full, as given) that are folders on disk now.</summary>
    public static HashSet<string> Folders(IEnumerable<string> fullPaths)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in fullPaths.Distinct(StringComparer.Ordinal).GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.Ordinal))
        {
            var paths = group.ToList();
            HashSet<string>? names = null;
            if (paths.Count >= ListAtLeast && group.Key.Length > 0)
            {
                DiskCallsForTest++;
                try { names = new HashSet<string>(Directory.EnumerateDirectories(group.Key).Select(Path.GetFileName)!, NameComparer); }
                catch (DirectoryNotFoundException) { continue; } // the parent is gone: so is everything in it
                catch (IOException) { }                          // anything else: ask each, as below
                catch (UnauthorizedAccessException) { }
            }
            foreach (var p in paths)
            {
                bool isFolder;
                if (names is not null) isFolder = names.Contains(Path.GetFileName(p));
                else { DiskCallsForTest++; isFolder = Directory.Exists(p); }
                if (isFolder) folders.Add(p);
            }
        }
        return folders;
    }
}
