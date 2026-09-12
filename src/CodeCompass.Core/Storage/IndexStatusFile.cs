using System.Text.Json;

namespace CodeCompass.Core.Storage;

/// <summary>A snapshot of a repo's index state, written by the MCP server to the local cache and read
/// by the <c>codecompass statusline</c> command (a separate short-lived process can't see the server's
/// memory, so the state is bridged through this tiny file).</summary>
public sealed record IndexStatus(string State, string Text, int Files);

/// <summary>
/// Reads/writes the per-repo <c>status.json</c> in the local index cache. The server publishes its
/// current state here on transitions; the status-line command reads it. Best-effort on both sides -
/// a missing/invalid file just means "no status to show". Lives next to the index (local disk), so
/// reading it never touches the source (which may be a slow network share).
/// </summary>
public static class IndexStatusFile
{
    private const string Name = "status.json";

    public static string PathFor(string repoRoot) => Path.Combine(IndexStore.GetCacheDir(repoRoot), Name);

    public static void Write(string repoRoot, IndexStatus status)
    {
        try
        {
            var json = JsonSerializer.Serialize(status);
            AtomicFile.Write(PathFor(repoRoot), s =>
            {
                using var w = new StreamWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
                w.Write(json);
            });
        }
        catch { /* status is best-effort; never let it break indexing */ }
    }

    public static IndexStatus? Read(string repoRoot)
    {
        try
        {
            // Read-only: resolve the path WITHOUT creating the cache dir (the status-line command runs
            // on every render, for any folder Claude is in - most of which we've never indexed).
            var path = Path.Combine(IndexStore.CacheDirPath(repoRoot), Name);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<IndexStatus>(File.ReadAllText(path));
        }
        catch { return null; }
    }
}
