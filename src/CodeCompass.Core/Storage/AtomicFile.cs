using System.Linq;
using System.Text;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Path-safety checks for values read back from on-disk index metadata (manifests, doc tables).
/// A corrupt or tampered cache must not be able to make the tool read/return files outside the
/// repo or write outside the cache directory.
/// </summary>
public static class PathSafety
{
    /// <summary>A relative, in-repo path: not rooted and with no ".." segment.</summary>
    public static bool IsInsideRepo(string rel) =>
        rel.Length > 0 && !Path.IsPathRooted(rel) && !rel.Split('/', '\\').Any(p => p == "..");

    /// <summary>A bare filename (no directory separators, not rooted) - resolves only inside its dir.</summary>
    public static bool IsBareFileName(string name) =>
        name.Length > 0 && name == Path.GetFileName(name);
}

/// <summary>
/// Small IO helpers shared by the on-disk index/snapshot code, so the invariants (atomic
/// replace, tolerate-still-mapped deletes, monotonic file numbering) live in one place instead
/// of being copy-pasted across every segment/manifest/tombstone/journal writer.
/// </summary>
public static class AtomicFile
{
    /// <summary>Write via a temp file + atomic replace, so a crash never leaves a truncated file.</summary>
    public static void Write(string path, Action<Stream> writeBody)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            writeBody(fs);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Atomic write for text content (manifests).</summary>
    public static void WriteText(string path, Action<TextWriter> writeBody)
    {
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, append: false))
            writeBody(w);
        File.Move(tmp, path, overwrite: true);
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
