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
/// definition that could live in one. Both default to 0 for an older meta that predates them.</summary>
public sealed record IndexMeta(string Root, string Version, string BuiltUtc, int Files, int FilesOverCap = 0, int FilesSymbolSkipped = 0);

public static class IndexMetaFile
{
    private const string Name = "meta.json";

    /// <summary>Stamp version + timestamp automatically; callers pass only what they know.</summary>
    public static void Write(string repoRoot, int files, int filesOverCap = 0, int filesSymbolSkipped = 0)
    {
        try
        {
            var meta = new IndexMeta(Path.GetFullPath(repoRoot), BuildInfo.Version,
                DateTime.UtcNow.ToString("o"), files, filesOverCap, filesSymbolSkipped);
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
}
