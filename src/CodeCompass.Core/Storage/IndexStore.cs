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
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Path.GetTempPath(), "CodeCompass-cache");
        return Path.Combine(baseDir, "CodeCompass");
    }

    /// <summary>Stable short key for a repo path (used for its cache dir and per-repo log name).</summary>
    public static string RepoKey(string repoRoot) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repoRoot).ToLowerInvariant())))[..16];

    public static string GetCacheDir(string repoRoot)
    {
        var dir = Path.Combine(BaseDir(), RepoKey(repoRoot));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string IndexPath(string repoRoot) => Path.Combine(GetCacheDir(repoRoot), "trigram.idx");

    public static string SymbolIndexPath(string repoRoot) => Path.Combine(GetCacheDir(repoRoot), "symbols.idx");

    public static string SnapshotPath(string repoRoot) => Path.Combine(GetCacheDir(repoRoot), "snapshot.bin");
}
