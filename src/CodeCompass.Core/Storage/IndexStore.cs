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
    public static string GetCacheDir(string repoRoot)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repoRoot.ToLowerInvariant())))[..16];

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Path.GetTempPath(), "CodeCompass-cache");

        var dir = Path.Combine(baseDir, "CodeCompass", key);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string IndexPath(string repoRoot) => Path.Combine(GetCacheDir(repoRoot), "trigram.idx");
}
