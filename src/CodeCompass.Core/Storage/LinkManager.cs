using System.Linq;
using CodeCompass.Core.Config;
using CodeCompass.Core.Indexing;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Add / remove / list linked roots for a project - the SHARED logic behind BOTH the CLI <c>link</c>
/// commands and the MCP <c>manage_links</c> tool, so the two can't drift. It returns structured outcomes;
/// each host wraps them in its own presentation (the CLI with interactive prompts + stdout, the MCP tool
/// with a text summary). The DECISIONS live here: the nesting guards, the index-or-reuse-or-defer policy on
/// add, and the shared-index safety check on remove. See <see cref="LinkStore"/> for the storage layer.
/// </summary>
public static class LinkManager
{
    public enum AddStatus { Rejected, AlreadyLinked, ReusedIndex, Indexed, DeferredTooBig, IndexFailed }
    public sealed record AddResult(AddStatus Status, string Message);

    /// <summary>Attach <paramref name="linked"/> to <paramref name="project"/>: reject overlaps, record the
    /// link, then reuse an existing index / build one / defer if it's over the auto-index size limit.</summary>
    public static AddResult Add(string project, string linked)
    {
        project = Path.GetFullPath(project);
        linked = Path.GetFullPath(linked);
        if (!Directory.Exists(linked)) return new(AddStatus.Rejected, $"not a directory: {linked}");
        var existingLinks = LinkStore.Read(project);
        // Exact re-add is "already linked" (clearer than the self-overlap message the nesting check would give).
        if (existingLinks.Any(r => string.Equals(Path.GetFullPath(r), linked, StringComparison.OrdinalIgnoreCase)))
            return new(AddStatus.AlreadyLinked, $"already linked: {linked}");
        if (Nested(project, linked, out var why)) return new(AddStatus.Rejected, "cannot link: " + why);
        foreach (var existing in existingLinks)
            if (Nested(existing, linked, out var why2)) return new(AddStatus.Rejected, "cannot link: " + why2);

        if (!LinkStore.Add(project, linked)) return new(AddStatus.AlreadyLinked, $"already linked: {linked}");

        // Reuse an existing index (the shared-root case), else apply the linked root's own size policy.
        if (RepositoryIndexer.TryLoad(linked, out var t0, out var s0))
        {
            t0.Dispose(); s0.Dispose();
            return new(AddStatus.ReusedIndex, $"linked: {linked} (index already present - reused)");
        }
        CodeCompassConfig.Load(linked); // the linked root's own .codecompass.json governs its build/size gate
        if (RepositoryIndexer.ExceedsAutoLimit(linked, out _))
        {
            var limitMb = CodeCompassConfig.MaxAutoBytes(CodeCompassConfig.Current) / 1048576.0;
            return new(AddStatus.DeferredTooBig,
                $"linked: {linked} (over the {limitMb:F0} MB auto-index limit - build it once with:  codecompass index \"{linked}\")");
        }
        try
        {
            var (ti, sy, st) = RepositoryIndexer.Build(linked);
            ti.Dispose(); sy.Dispose();
            return new(AddStatus.Indexed,
                $"linked: {linked} (indexed {st.Files:N0} files, {st.Bytes / 1048576.0:F1} MB, {st.Symbols:N0} symbols in {st.Seconds:F1}s)");
        }
        catch (Exception ex)
        {
            return new(AddStatus.IndexFailed, $"linked: {linked} (attached, but indexing failed: {ex.Message} - build with:  codecompass index \"{linked}\")");
        }
    }

    public enum RemoveStatus { NotLinked, UnlinkedShared, UnlinkedNoIndex, UnlinkedPurged, UnlinkedKept, PurgeFailed }
    public sealed record RemoveResult(RemoveStatus Status, string Message, IReadOnlyList<string> OtherProjects);

    /// <summary>Detach <paramref name="linked"/>. Its shared index is kept if another project still links it;
    /// otherwise <paramref name="confirmPurge"/> (given the index's cache dir) decides whether to delete it -
    /// the CLI prompts the user, the MCP tool passes a fixed flag. Only invoked when a purge is actually
    /// possible (not shared, index present), so the host never prompts pointlessly.</summary>
    public static RemoveResult Remove(string project, string linked, Func<string, bool>? confirmPurge)
    {
        project = Path.GetFullPath(project);
        linked = Path.GetFullPath(linked);
        if (!LinkStore.Remove(project, linked))
            return new(RemoveStatus.NotLinked, $"not linked to this project: {linked}", Array.Empty<string>());

        var others = LinkStore.ProjectsLinking(linked, excludingProjectRoot: project);
        if (others.Count > 0)
            return new(RemoveStatus.UnlinkedShared, $"unlinked: {linked} (index kept - still linked by {others.Count} other project(s))", others);

        var cacheDir = IndexStore.CacheDirPath(linked);
        if (!Directory.Exists(cacheDir)) return new(RemoveStatus.UnlinkedNoIndex, $"unlinked: {linked}", Array.Empty<string>());
        if (confirmPurge is null || !confirmPurge(cacheDir))
            return new(RemoveStatus.UnlinkedKept, $"unlinked: {linked} (index kept at {cacheDir}; remove with purge to reclaim disk)", Array.Empty<string>());
        try
        {
            Directory.Delete(cacheDir, recursive: true);
            return new(RemoveStatus.UnlinkedPurged, $"unlinked: {linked} (index deleted)", Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new(RemoveStatus.PurgeFailed, $"unlinked: {linked} (could not delete index: {ex.Message})", Array.Empty<string>());
        }
    }

    public sealed record LinkInfo(string Path, string Status, bool Exists, bool Indexed);

    /// <summary>The project's linked roots with existence + index status (for `link list` / `manage_links list`).</summary>
    public static IReadOnlyList<LinkInfo> List(string project)
    {
        project = Path.GetFullPath(project);
        var result = new List<LinkInfo>();
        foreach (var l in LinkStore.Read(project))
        {
            if (!Directory.Exists(l)) { result.Add(new(l, "MISSING (directory gone)", false, false)); continue; }
            if (RepositoryIndexer.TryLoad(l, out var t, out var s))
            {
                // DocumentCount is the LIVE count (segment docs - tombstones) as of NOW; the "built by" stamp
                // is when the index was last built. They make explicit that this is a snapshot at read time, so
                // a difference from `codecompass index` (a build-run stat) reads as expected, not a discrepancy.
                using (t) using (s)
                {
                    var meta = IndexMetaFile.Read(l);
                    var built = meta is not null ? $"; built by {meta.Version} at {FormatUtc(meta.BuiltUtc)}" : "";
                    result.Add(new(l, $"indexed, {t.DocumentCount:N0} files (as of {System.DateTime.UtcNow:u}{built})", true, true));
                }
            }
            else result.Add(new(l, $"not indexed - run: codecompass index \"{l}\"", true, false));
        }
        return result;
    }

    // Render an ISO-8601 ("o") build timestamp as a compact UTC "u" string; fall back to raw if it won't parse.
    private static string FormatUtc(string iso) =>
        System.DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime().ToString("u") : iso;

    // True if `a` and `b` are the same directory or one is nested in the other (so linking `b` is redundant).
    private static bool Nested(string a, string b, out string why)
    {
        if (PathSafety.IsUnderOrEqual(b, a) && PathSafety.IsUnderOrEqual(a, b)) { why = $"{b} is the project or an existing linked root itself."; return true; }
        if (PathSafety.IsUnderOrEqual(b, a)) { why = $"{b} is already inside {a} - it's covered by that index."; return true; }
        if (PathSafety.IsUnderOrEqual(a, b)) { why = $"{a} is inside {b} - link the outer directory instead of nesting."; return true; }
        why = "";
        return false;
    }
}
