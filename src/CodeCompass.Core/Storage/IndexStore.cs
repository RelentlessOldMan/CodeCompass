using System.Security.Cryptography;
using System.Text;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Resolves where a repo's index lives. Indexes are kept in a central per-user cache
/// (%LOCALAPPDATA%\CodeCompass\&lt;key&gt;) keyed by the repo's absolute path, so indexed
/// repos stay clean and there is one place to manage or clear.
/// </summary>
public static class IndexStore
{
    /// <summary>The per-user CodeCompass root (%LOCALAPPDATA%\CodeCompass), holding index
    /// caches and the shared <c>logs</c> folder. One place to manage or clear everything.</summary>
    public static string BaseDir()
    {
        // Explicit override (used by the test suite to keep index caches out of the real
        // %LOCALAPPDATA%\CodeCompass, and available to anyone who wants the cache on another drive).
        var overrideDir = Environment.GetEnvironmentVariable("CODECOMPASS_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Path.GetTempPath(), "CodeCompass-cache");
        return Path.Combine(baseDir, "CodeCompass");
    }

    /// <summary>Stable short key for a repo path (used for its cache dir and per-repo log name). Normalized like every
    /// other root identity (PathSafety.NormalizeDir): "C:\repo" and "C:\repo\" used to hash to two separate caches -
    /// two diverging indexes of one repo (e.g. a Claude Code and a Codex session spelling the root differently).</summary>
    public static string RepoKey(string repoRoot) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(PathSafety.NormalizeDir(repoRoot).ToLowerInvariant())))[..16];

    public static string GetCacheDir(string repoRoot)
    {
        var dir = CacheDirPath(repoRoot);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The repo's cache directory path WITHOUT creating it. For read-only callers (e.g. the
    /// status-line command, which runs on every render) so they don't litter the cache root with an
    /// empty dir for every folder Claude visits.</summary>
    public static string CacheDirPath(string repoRoot) => Path.Combine(BaseDir(), RepoKey(repoRoot));

    public static string IndexPath(string repoRoot) => Path.Combine(GetCacheDir(repoRoot), "trigram.idx");

    public static string SymbolIndexPath(string repoRoot) => Path.Combine(GetCacheDir(repoRoot), "symbols.idx");

    public static string SnapshotPath(string repoRoot) => Path.Combine(GetCacheDir(repoRoot), "snapshot.bin");

    // Suffix of a cache directory moved aside to be deleted (see TryClearCacheDir). Never a live cache.
    private const string ClearingMarker = ".clearing-";

    /// <summary>True for a cache-root entry that is a cache moved aside for deletion, not a live cache.</summary>
    public static bool IsClearingLeftover(string dir) => Path.GetFileName(dir).Contains(ClearingMarker, StringComparison.Ordinal);

    /// <summary>Delete one repo's cache directory ALL OR NOTHING. A file-by-file delete under a running server stopped at
    /// the first file the server held open and left a half-deleted index it kept serving (field report). So the directory
    /// is first moved aside in one step - Windows refuses to rename a directory while a file in it is open without delete
    /// sharing, which is how every index file is opened - and only then deleted. If the move is refused, nothing was
    /// touched: false, with <paramref name="error"/> saying the cache is in use. Once moved, the cache is gone from every
    /// reader's view; a leftover the delete couldn't finish is reported in <paramref name="error"/> and removed by gc.</summary>
    public static bool TryClearCacheDir(string dir, out string error)
    {
        error = "";
        var aside = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ClearingMarker + Guid.NewGuid().ToString("N")[..8];
        try { Directory.Move(dir, aside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "the cache is in use (a CodeCompass server or watcher has it open), so nothing was deleted - " +
                    $"close it and retry ({ex.Message})";
            return false;
        }
        try { Directory.Delete(aside, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"cleared, but a leftover could not be removed yet ({aside}: {ex.Message}); `codecompass cache gc` removes it";
        }
        return true;
    }
}
