using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Ignore;

namespace CodeCompass.Core.Walking;

/// <summary>A candidate file found by the walker. RelativePath is normalized with '/'. <see cref="MTimeTicks"/>
/// is the last-write time (UTC ticks) read from the directory enumeration - carried here so callers don't
/// re-stat the file for its timestamp (a second network round-trip over SMB).</summary>
public readonly record struct FileRecord(string RelativePath, string FullPath, long Size, long MTimeTicks);

/// <summary>
/// Iterative directory walk that prunes ignored directories before descending and
/// skips ignored/oversized files, so it never pays to enumerate junk. Symlinks and
/// junctions (reparse points) are skipped to avoid cycles.
/// </summary>
public sealed class FileWalker
{
    private readonly IgnoreRules _ignore;

    public FileWalker(IgnoreRules ignore) => _ignore = ignore;

    /// <summary>Files skipped for exceeding the size cap during the last <see cref="Walk"/> (they are
    /// silently absent from the index otherwise). Valid after enumeration completes.</summary>
    public int OverCapSkipped { get; private set; }
    public long LargestOverCapBytes { get; private set; }
    public string? LargestOverCapPath { get; private set; }

    public IEnumerable<FileRecord> Walk(string root)
    {
        root = Path.GetFullPath(root);
        OverCapSkipped = 0;
        LargestOverCapBytes = 0;
        LargestOverCapPath = null;
        // Read size/mtime/attributes off the enumerated FileInfo/DirectoryInfo objects: EnumerateFileSystemInfos
        // pre-fills them from the single directory listing (find-data) and caches them, so it's ONE round-trip
        // per directory instead of extra per-file stats for size and mtime - the difference between fast and
        // ~minutes over an SMB share. AttributesToSkip=0 preserves coverage (default would drop Hidden/System);
        // IgnoreInaccessible skips unreadable children instead of failing the whole directory.
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            List<FileSystemInfo> entries;
            try
            {
                // Materialize under the try so a mid-iteration failure on this directory is caught here
                // (one bad subtree is logged and skipped, not fatal) - never inside the yielding loop below.
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList();
            }
            catch (DirectoryNotFoundException) { continue; } // vanished between enqueue and read: fine
            catch (Exception ex)
            {
                // Access denied, a path too long for the OS, etc. Skipping means files under
                // this directory silently won't be indexed - surface it so it's diagnosable.
                Log.For(root).Warn($"walk skipped a directory subtree: {dir} ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            foreach (var info in entries)
            {
                if (info is DirectoryInfo sub)
                {
                    if (_ignore.IsIgnoredDirectory(sub.Name)) continue;
                    try
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue; // cached from enumeration
                    }
                    catch { continue; }
                    stack.Push(sub.FullName);
                }
                else if (info is FileInfo file)
                {
                    long size;
                    try { size = file.Length; } // cached from enumeration - no extra round-trip
                    catch { continue; }

                    if (size > _ignore.MaxFileSizeBytes) // count over-cap skips so the coverage gap isn't silent
                    {
                        OverCapSkipped++;
                        if (size > LargestOverCapBytes) { LargestOverCapBytes = size; LargestOverCapPath = file.FullName; }
                        continue;
                    }

                    if (_ignore.IsIgnoredFile(file.Name, size)) continue;

                    long mtime;
                    try { mtime = file.LastWriteTimeUtc.Ticks; } catch { mtime = 0; } // cached from enumeration
                    var rel = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                    yield return new FileRecord(rel, file.FullName, size, mtime);
                }
            }
        }
    }
}
