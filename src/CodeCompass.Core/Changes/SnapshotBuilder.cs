using System.Security.Cryptography;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Changes;

/// <summary>
/// Builds a content snapshot (relative path -> FileState) of a tree. The snapshot is
/// what we hand to <see cref="MerkleTree"/> and diff across syncs to find real changes.
/// </summary>
public static class SnapshotBuilder
{
    public static Dictionary<string, FileState> Build(string root, FileWalker walker)
    {
        var result = new Dictionary<string, FileState>(StringComparer.Ordinal);
        foreach (var f in walker.Walk(root))
        {
            string hash;
            long mtime;
            try
            {
                hash = HashFile(f.FullPath);
                mtime = File.GetLastWriteTimeUtc(f.FullPath).Ticks;
            }
            catch { continue; }
            result[f.RelativePath] = new FileState(f.Size, mtime, hash);
        }
        return result;
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
