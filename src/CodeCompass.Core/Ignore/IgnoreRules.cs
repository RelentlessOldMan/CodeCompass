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
        "coverage", ".codecompass", ".next", ".nuget",
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

    private readonly HashSet<string> _ignoredDirs;
    private readonly HashSet<string> _ignoredExtensions;

    public long MaxFileSizeBytes { get; }

    public IgnoreRules(long maxFileSizeBytes = 5 * 1024 * 1024, IEnumerable<string>? extraIgnoredDirs = null)
    {
        _ignoredDirs = new HashSet<string>(DefaultIgnoredDirs, StringComparer.OrdinalIgnoreCase);
        if (extraIgnoredDirs is not null)
            foreach (var d in extraIgnoredDirs)
                _ignoredDirs.Add(d);
        _ignoredExtensions = DefaultIgnoredExtensions;
        MaxFileSizeBytes = maxFileSizeBytes;
    }

    public bool IsIgnoredDirectory(string directoryName) => _ignoredDirs.Contains(directoryName);

    public bool IsIgnoredFile(string fileName, long size)
    {
        if (size > MaxFileSizeBytes) return true;
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && _ignoredExtensions.Contains(ext);
    }

    /// <summary>A NUL byte in the leading chunk is a reliable "this is binary" signal.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        foreach (var b in head)
            if (b == 0)
                return true;
        return false;
    }
}
