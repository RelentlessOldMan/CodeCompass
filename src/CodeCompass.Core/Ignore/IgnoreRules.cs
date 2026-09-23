using CodeCompass.Core.Config;

namespace CodeCompass.Core.Ignore;

/// <summary>
/// Decides what the walker skips: noise directories, binary/asset file types, and
/// oversized files. Content-based binary detection lives here too. This is the first
/// line of defense against indexing "garbage" (build output, dependencies, blobs).
/// </summary>
public sealed class IgnoreRules
{
    // Directory names skipped entirely; we never descend into them.
    private static readonly HashSet<string> DefaultIgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".vscode", ".idea",
        "bin", "obj", "node_modules", "packages", "dist", "build", "out", "target",
        ".gradle", "__pycache__", ".pytest_cache", ".mypy_cache", "venv", ".venv",
        "coverage", ".codecompass", ".corpus", ".next", ".nuget",
        // Other AI/code tools' own index+cache dirs. Indexing these means searching a rival tool's
        // dumped tag/symbol database - e.g. .claude/index/tags.json is one giant file of every
        // identifier, so a search for any common name returns hundreds of junk hits from it.
        ".claude", ".cursor", ".aider", ".serena", ".continue",
    };

    // Extensions we never index (binary / assets). Lowercase, leading dot.
    private static readonly HashSet<string> DefaultIgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".a", ".lib", ".o", ".obj", ".class", ".jar",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp", ".tif", ".tiff",
        ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac", ".ogg", ".webm", ".mkv",
        ".zip", ".gz", ".tar", ".7z", ".rar", ".bz2", ".xz", ".zst",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".bin", ".dat", ".db", ".sqlite", ".pack", ".idx",
    };

    public const long DefaultMaxFileSizeBytes = 5 * 1024 * 1024;

    private readonly HashSet<string> _ignoredDirs;
    private readonly HashSet<string> _ignoredExtensions;

    public long MaxFileSizeBytes { get; }

    /// <summary>
    /// Build ignore rules. When <paramref name="maxFileSizeBytes"/> is null the cap comes from
    /// CODECOMPASS_MAX_FILE_MB (default 5 MB); CODECOMPASS_IGNORE (comma/semicolon-separated
    /// directory names) always adds to the skipped-directory set. These env knobs let an operator
    /// exclude a pathological generated tree (e.g. dense register-map headers) or lower the cap
    /// without editing source.
    /// </summary>
    public IgnoreRules(long? maxFileSizeBytes = null, IEnumerable<string>? extraIgnoredDirs = null)
    {
        _ignoredDirs = new HashSet<string>(DefaultIgnoredDirs, StringComparer.OrdinalIgnoreCase);
        foreach (var d in CodeCompassConfig.IgnoredDirs()) _ignoredDirs.Add(d); // env + .codecompass.json
        if (extraIgnoredDirs is not null)
            foreach (var d in extraIgnoredDirs)
                _ignoredDirs.Add(d);
        _ignoredExtensions = DefaultIgnoredExtensions;
        MaxFileSizeBytes = maxFileSizeBytes ?? CodeCompassConfig.MaxFileBytes();
    }

    public bool IsIgnoredDirectory(string directoryName) => _ignoredDirs.Contains(directoryName);

    public bool IsIgnoredFile(string fileName, long size)
    {
        if (size > MaxFileSizeBytes) return true;
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && _ignoredExtensions.Contains(ext);
    }

    /// <summary>A NUL byte in the leading chunk is a reliable "this is binary" signal - EXCEPT for
    /// UTF-16/UTF-32 text, whose ASCII characters carry NUL bytes. A recognized Unicode byte-order mark
    /// means the file is text in that encoding, so we let it through (the decoders detect the same BOM and
    /// read it correctly). Without a BOM, UTF-16/32 is indistinguishable from binary here, so a NUL still
    /// reads as binary - that BOM-less case stays excluded (a documented limitation).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (HasTextBom(head)) return false;
        foreach (var b in head)
            if (b == 0)
                return true;
        return false;
    }

    // A leading UTF-8 / UTF-16 / UTF-32 byte-order mark - the reliable "this is Unicode text" signal.
    private static bool HasTextBom(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00) return true; // UTF-32 LE
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF) return true; // UTF-32 BE
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return true;                                 // UTF-16 LE
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return true;                                 // UTF-16 BE
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return true;                 // UTF-8
        return false;
    }
}
