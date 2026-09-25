using System.Text.Json;
using CodeCompass.Core.Diagnostics;

namespace CodeCompass.Core.Storage;

/// <summary>Small per-repo record written next to the index: the absolute repo root, the CodeCompass
/// version that built it, when, the file count, and how many files were skipped over the size cap
/// (absent from the index entirely). The cache dir is named by a hash of the path, so this is the only
/// place the original path is recorded - it lets <c>codecompass cache</c> list/gc caches by real path,
/// lets <c>doctor</c>/<c>report</c> show "built by version X at time Y", and lets the search tools tell
/// the truth about a zero result (a match could live in a file the caps excluded). <see cref="FilesOverCap"/>
/// counts files excluded from the index ENTIRELY by the file-size cap; <see cref="FilesSymbolSkipped"/>
/// counts files that ARE indexed for text search but had NO symbols extracted (over the symbol cap,
/// classified as a numeric data blob, or streamed) - so a go-to-definition zero can be honest about a
/// definition that could live in one. Both default to 0 for an older meta that predates them.
/// <see cref="ContentVersion"/> records <see cref="BuildInfo.IndexerContentVersion"/> at build time, so a
/// later binary can tell whether a rebuild would actually change results (vs. a mere product-version bump);
/// it defaults to 0 for an older meta, which equals the baseline, so pre-stamp indexes are never falsely
/// flagged stale.</summary>
public sealed record IndexMeta(string Root, string Version, string BuiltUtc, int Files, int FilesOverCap = 0, int FilesSymbolSkipped = 0, int ContentVersion = 0);

public static class IndexMetaFile
{
    private const string Name = "meta.json";

    /// <summary>Stamp version + timestamp automatically; callers pass only what they know.</summary>
    public static void Write(string repoRoot, int files, int filesOverCap = 0, int filesSymbolSkipped = 0)
    {
        try
        {
            var meta = new IndexMeta(Path.GetFullPath(repoRoot), BuildInfo.Version,
                DateTime.UtcNow.ToString("o"), files, filesOverCap, filesSymbolSkipped, BuildInfo.IndexerContentVersion);
            AtomicFile.WriteText(Path.Combine(IndexStore.GetCacheDir(repoRoot), Name), w => w.Write(JsonSerializer.Serialize(meta)));
        }
        catch { /* best-effort; never break a build over metadata */ }
    }

    /// <summary>Read a cache dir's meta (null if absent/invalid). Takes the cache dir directly so
    /// <c>cache</c> can read it without knowing the repo path.</summary>
    public static IndexMeta? ReadFromCacheDir(string cacheDir)
    {
        try
        {
            var path = Path.Combine(cacheDir, Name);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<IndexMeta>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static IndexMeta? Read(string repoRoot) => ReadFromCacheDir(IndexStore.CacheDirPath(repoRoot));

    /// <summary>Was this index built by an indexer whose OUTPUT logic is behind the running binary - so a
    /// rebuild would materially change results? Judged on <see cref="IndexMeta.ContentVersion"/> vs
    /// <see cref="BuildInfo.IndexerContentVersion"/>, NOT the product version (which bumps every commit).
    /// False for a null meta (unknown, don't cry wolf) or a current/newer index. <paramref name="builtWith"/>
    /// / <paramref name="current"/> return the two versions for the message.</summary>
    public static bool IndexerBehind(IndexMeta? meta, out int builtWith, out int current)
    {
        builtWith = meta?.ContentVersion ?? 0;
        current = BuildInfo.IndexerContentVersion;
        return meta is not null && builtWith < current;
    }
}
