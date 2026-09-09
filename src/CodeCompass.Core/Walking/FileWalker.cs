using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Ignore;

namespace CodeCompass.Core.Walking;

/// <summary>A candidate file found by the walker. RelativePath is normalized with '/'.</summary>
public readonly record struct FileRecord(string RelativePath, string FullPath, long Size);

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
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subdirs, files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (DirectoryNotFoundException) { continue; } // vanished between enqueue and read: fine
            catch (Exception ex)
            {
                // Access denied, a path too long for the OS, etc. Skipping means files under
                // this directory silently won't be indexed - surface it so it's diagnosable.
                Log.For(root).Warn($"walk skipped a directory subtree: {dir} ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (_ignore.IsIgnoredDirectory(name)) continue;
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                stack.Push(sub);
            }

            foreach (var file in files)
            {
                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; }

                if (size > _ignore.MaxFileSizeBytes) // count over-cap skips so the coverage gap isn't silent
                {
                    OverCapSkipped++;
                    if (size > LargestOverCapBytes) { LargestOverCapBytes = size; LargestOverCapPath = file; }
                    continue;
                }

                var name = Path.GetFileName(file);
                if (_ignore.IsIgnoredFile(name, size)) continue;

                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                yield return new FileRecord(rel, file, size);
            }
        }
    }
}
