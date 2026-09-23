using System.Linq;
using System.Text.Json;

namespace CodeCompass.Core.Storage;

/// <summary>
/// The EXTERNAL directories a project's index federates with - roots outside the project tree (a shared
/// library, a sibling project, a third-party drop on another drive) whose own indexes are queried
/// alongside the project's. Stored MACHINE-LOCAL as <c>links.json</c> in the PROJECT's cache dir: the
/// linked paths are absolute and machine-specific (Z:\..., another checkout), so they don't belong in a
/// committed, portable <c>.codecompass.json</c>. Each linked root keeps its OWN independent index (keyed
/// by its own path), so a root shared by several projects is indexed ONCE and reused - and unlinking is
/// just dropping it from this list (the shared index survives if another project still references it).
/// </summary>
public static class LinkStore
{
    private const string Name = "links.json";

    /// <summary>The absolute linked roots for a project (empty if none). Never throws.</summary>
    public static IReadOnlyList<string> Read(string projectRoot) =>
        ReadFromCacheDir(IndexStore.CacheDirPath(projectRoot));

    /// <summary>As <see cref="Read"/> but from a cache dir directly (for the cross-project scan).</summary>
    public static IReadOnlyList<string> ReadFromCacheDir(string cacheDir)
    {
        try
        {
            var path = Path.Combine(cacheDir, Name);
            if (!File.Exists(path)) return Array.Empty<string>();
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Add a linked root (absolute, deduped). Returns false if already present.</summary>
    public static bool Add(string projectRoot, string linkedRoot)
    {
        linkedRoot = Normalize(linkedRoot);
        var roots = Read(projectRoot).ToList();
        if (roots.Any(r => PathsEqual(r, linkedRoot))) return false;
        roots.Add(linkedRoot);
        Save(projectRoot, roots);
        return true;
    }

    /// <summary>Drop a linked root. Returns false if it wasn't linked.</summary>
    public static bool Remove(string projectRoot, string linkedRoot)
    {
        linkedRoot = Normalize(linkedRoot);
        var roots = Read(projectRoot).ToList();
        if (roots.RemoveAll(r => PathsEqual(r, linkedRoot)) == 0) return false;
        Save(projectRoot, roots);
        return true;
    }

    /// <summary>The root paths of OTHER projects that also link <paramref name="linkedRoot"/> - so a
    /// caller can decide whether deleting the shared index is safe. Scans the per-user cache base dir and
    /// resolves each project's real path from its meta.json. Excludes <paramref name="excludingProjectRoot"/>.</summary>
    public static IReadOnlyList<string> ProjectsLinking(string linkedRoot, string? excludingProjectRoot = null)
    {
        linkedRoot = Normalize(linkedRoot);
        var result = new List<string>();
        var baseDir = IndexStore.BaseDir();
        if (!Directory.Exists(baseDir)) return result;
        var exclKey = excludingProjectRoot is null ? null : IndexStore.RepoKey(excludingProjectRoot);

        foreach (var cacheDir in Directory.EnumerateDirectories(baseDir))
        {
            if (exclKey is not null && string.Equals(Path.GetFileName(cacheDir), exclKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ReadFromCacheDir(cacheDir).Any(r => PathsEqual(r, linkedRoot))) continue;
            var meta = IndexMetaFile.ReadFromCacheDir(cacheDir);
            result.Add(meta?.Root ?? cacheDir);
        }
        return result;
    }

    private static void Save(string projectRoot, IReadOnlyList<string> roots) =>
        AtomicFile.WriteText(Path.Combine(IndexStore.GetCacheDir(projectRoot), Name),
                             w => w.Write(JsonSerializer.Serialize(roots)));

    private static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}
