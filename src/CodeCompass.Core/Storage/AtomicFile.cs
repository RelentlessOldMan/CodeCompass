using System.Text;
using System.Threading;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Small IO helpers shared by the on-disk index/snapshot code, so the invariants (atomic
/// replace, tolerate-still-mapped deletes, monotonic file numbering) live in one place instead
/// of being copy-pasted across every segment/manifest/tombstone/journal writer.
/// </summary>
public static class AtomicFile
{
    private const int ReplaceAttempts = 5;
    private const int ReplaceBackoffMs = 30;

    /// <summary>Write via a temp file + atomic replace, so a crash never leaves a truncated file. The temp is
    /// flushed to disk before the rename (so the rename can't be persisted ahead of the contents on power
    /// loss) and is deleted if the write or rename fails (so a full disk / still-locked target doesn't leave
    /// an orphan <c>.tmp</c>).</summary>
    public static void Write(string path, Action<Stream> writeBody)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                writeBody(fs);
                fs.Flush(flushToDisk: true);
            }
            ReplaceWithRetry(tmp, path);
        }
        catch { TryDelete(tmp); throw; }
    }

    /// <summary>Atomic write for text content (manifests). Same crash/leak guarantees as <see cref="Write"/>.</summary>
    public static void WriteText(string path, Action<TextWriter> writeBody)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var w = new StreamWriter(fs, leaveOpen: true)) writeBody(w);
                fs.Flush(flushToDisk: true);
            }
            ReplaceWithRetry(tmp, path);
        }
        catch { TryDelete(tmp); throw; }
    }

    /// <summary>Move <paramref name="tmp"/> onto <paramref name="path"/> as close to atomically as the platform
    /// allows, retrying transient failures. Prefers <see cref="File.Replace(string,string,string?)"/> when the
    /// target exists: it swaps the file in place (via an internal backup) rather than delete-then-rename, so a
    /// crash or SMB disconnect mid-replace leaves EITHER the old OR the new file - never neither. (A plain
    /// <c>File.Move(overwrite:true)</c> over a share is a non-atomic delete+rename that can wipe the target with
    /// no replacement - the failure mode this guards. A brief AV/indexer/reader lock, or a share hiccup under
    /// load, is retried with backoff.)</summary>
    private static void ReplaceWithRetry(string tmp, string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                if (!File.Exists(path)) { File.Move(tmp, path); return; } // no target yet: a plain move is atomic
                try { File.Replace(tmp, path, destinationBackupFileName: null); return; }
                // Some filesystems/configs (cross-volume, certain SMB/FAT) don't support Replace -> fall back.
                catch (PlatformNotSupportedException) { File.Move(tmp, path, overwrite: true); return; }
                catch (NotSupportedException) { File.Move(tmp, path, overwrite: true); return; }
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < ReplaceAttempts - 1)
            {
                Thread.Sleep(ReplaceBackoffMs * (i + 1)); // transient sharing violation / share hiccup: back off + retry
            }
        }
    }

    /// <summary>Delete if present, tolerating a file that's still memory-mapped or already gone.</summary>
    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* locked/gone: ignore */ }
    }
}

/// <summary>Monotonic, never-reused numbering + orphan cleanup for numbered cache files
/// (<c>seg-00000123.ccseg</c>, <c>sym-*.ccsym</c>, <c>snapshot-*.base</c>).</summary>
public static class NumberedFiles
{
    /// <summary>Next unused number for a <c>&lt;prefix&gt;-&lt;NNNNNNNN&gt;.&lt;ext&gt;</c> family (max existing + 1).</summary>
    public static int Next(string dir, string pattern)
    {
        int max = -1;
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                int dash = name.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(name.AsSpan(dash + 1), out var n) && n > max) max = n;
            }
        return max + 1;
    }

    /// <summary>Best-effort delete of matching files not in <paramref name="keep"/> (still-mapped ones are left).</summary>
    public static void CleanupOrphans(string dir, string pattern, IReadOnlySet<string> keep)
    {
        foreach (var f in Directory.EnumerateFiles(dir, pattern))
            if (!keep.Contains(Path.GetFileName(f)))
                AtomicFile.TryDelete(f);
    }
}
