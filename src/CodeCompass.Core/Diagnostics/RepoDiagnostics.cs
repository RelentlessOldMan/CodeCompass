using System.Runtime.InteropServices;
using CodeCompass.Core.Config;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Diagnostics;

/// <summary>
/// Gathers a human-readable diagnostic snapshot of a repo's index - version, environment, config,
/// index metadata, and the cache-directory listing. Deliberately records file PATHS/NAMES and SIZES
/// only, never file CONTENTS or source, so it's safe to hand to a maintainer for a bug report. Shared
/// by <c>codecompass doctor</c> (prints it) and <c>codecompass report</c> (writes it into the zip).
/// </summary>
public static class RepoDiagnostics
{
    /// <summary>One health check outcome: a name, whether it passed, and a short human detail.</summary>
    public readonly record struct Check(string Name, bool Ok, string Detail);

    private static string Mb(long b) => $"{b / 1048576.0:N1} MB";

    // An env-var name that hints its value is a secret, so the shareable report masks the value. Check the
    // part AFTER the CODECOMPASS_ prefix: the product name itself contains "PASS" (codecomPASS), so a raw
    // substring match would mask EVERY CODECOMPASS_* var - gutting the diagnostic dump while looking "safe".
    internal static bool LooksSecret(string name)
    {
        const string prefix = "CODECOMPASS_";
        var tail = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name.Substring(prefix.Length) : name;
        foreach (var marker in new[] { "TOKEN", "SECRET", "KEY", "PASS", "PWD", "CRED" })
            if (tail.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Write the full text report. Never throws (best-effort; notes anything it can't read).</summary>
    public static void WriteReport(TextWriter w, string root)
    {
        root = Path.GetFullPath(root);
        w.WriteLine($"CodeCompass diagnostics");
        w.WriteLine($"generated (UTC):  {DateTime.UtcNow:o}");
        w.WriteLine($"version:          {BuildInfo.Version}");
        w.WriteLine($"session:          {Log.SessionId}  (this report run; log lines are tagged [pid/session])");
        w.WriteLine();

        w.WriteLine("== environment ==");
        w.WriteLine($"os:               {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        w.WriteLine($"runtime:          {RuntimeInformation.FrameworkDescription}");
        w.WriteLine($"process arch:     {RuntimeInformation.ProcessArchitecture}");
        w.WriteLine($"cpu count:        {Environment.ProcessorCount}");
        try { w.WriteLine($"available memory: {Mb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)}"); } catch { }
        w.WriteLine("env (CODECOMPASS_*):");
        bool anyEnv = false;
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var k = e.Key?.ToString() ?? "";
            if (!k.StartsWith("CODECOMPASS_", StringComparison.OrdinalIgnoreCase)) continue;
            // The report bundle is meant to be shared. Print names always, but MASK the value of any var whose
            // name hints at a secret (a blind prefix dump would ship a token pasted into a CODECOMPASS_*_TOKEN
            // etc. straight into diagnostics.txt). Known knobs are numeric/path values and print verbatim.
            w.WriteLine($"    {k}={(LooksSecret(k) ? "<redacted>" : e.Value)}");
            anyEnv = true;
        }
        if (!anyEnv) w.WriteLine("    (none set)");
        w.WriteLine();

        w.WriteLine("== repo ==");
        w.WriteLine($"root:             {root}");
        w.WriteLine($"exists:           {Directory.Exists(root)}");
        w.WriteLine($"network path:     {NetworkPath.IsNetwork(root)}");
        var cfg = CodeCompassConfig.ReadFrom(root);
        w.WriteLine($".codecompass.json:{(cfg is null ? " (none - all defaults)" : " present")}");
        w.WriteLine();

        var cacheDir = IndexStore.CacheDirPath(root);
        w.WriteLine("== index ==");
        w.WriteLine($"cache dir:        {cacheDir}");
        w.WriteLine($"cache exists:     {Directory.Exists(cacheDir)}");

        var meta = IndexMetaFile.ReadFromCacheDir(cacheDir);
        if (meta is not null)
        {
            w.WriteLine($"built by version: {meta.Version}");
            w.WriteLine($"built (UTC):      {meta.BuiltUtc}");
            w.WriteLine($"files (at build): {meta.Files:N0}");
            if (meta.FilesOverCap > 0)
                w.WriteLine($"over size cap:    {meta.FilesOverCap:N0} file(s) NOT indexed (raise maxFileMb / run survey)");
            if (meta.FilesSymbolSkipped > 0)
                w.WriteLine($"symbols skipped:  {meta.FilesSymbolSkipped:N0} file(s) text-searchable but no symbols (over symbol cap / data blob / streamed)");
            if (meta.DroppedDirs > 0)
                w.WriteLine($"dirs dropped:     {meta.DroppedDirs:N0} director(y/ies) unreadable during the walk - their files are NOT indexed (re-run update/index; usually a transient network error)");
            if (!string.IsNullOrEmpty(meta.Landscape))
                w.WriteLine($"repo shape:       {meta.Landscape}");
            var scMb = meta.SidecarThresholdBytes / (1024.0 * 1024);
            w.WriteLine($"sidecar cutoff:   {scMb:F0} MB" +
                        (meta.SidecarThresholdBytes < RepoLandscape.DefaultSidecarThreshold
                            ? "  (lowered from 8 MB - network path with a mid-size tail, so 2-8 MB files get block-selective reads)"
                            : ""));
            // Total sidecar bytes vs the in-process cache budget: if the sidecars exceed the budget, a broad
            // query re-reads Blooms it can't keep cached (the search is slower but still correct). Surface it
            // so a user can raise CODECOMPASS_SIDECAR_CACHE_MB rather than wonder why repeats aren't faster.
            try
            {
                long scTotal = 0; int scCount = 0;
                if (Directory.Exists(cacheDir))
                    foreach (var f in Directory.EnumerateFiles(cacheDir, PositionalSidecar.Pattern))
                    { scTotal += new FileInfo(f).Length; scCount++; }
                if (scCount > 0)
                {
                    double totMb = scTotal / (1024.0 * 1024), budMb = SidecarCache.BudgetBytes / (1024.0 * 1024);
                    w.WriteLine($"sidecar cache:    {scCount:N0} file(s), {totMb:F0} MB total; in-process budget {budMb:F0} MB" +
                        (scTotal > SidecarCache.BudgetBytes
                            ? $"  (EXCEEDS budget - broad queries re-read some Blooms; set CODECOMPASS_SIDECAR_CACHE_MB >= {Math.Ceiling(totMb)} to fully cache)"
                            : "  (fits - repeat queries hit the cache)"));
                }
            }
            catch { /* diagnostics only */ }
            if (!string.Equals(Path.GetFullPath(meta.Root), root, StringComparison.OrdinalIgnoreCase))
                w.WriteLine($"[!] meta root differs: {meta.Root}");
        }
        else w.WriteLine("meta.json:        (none - index predates meta, or not built yet)");

        var status = IndexStatusFile.Read(root);
        if (status is not null) w.WriteLine($"status:           {status.State} - {status.Text}");
        w.WriteLine();

        // Linked external roots: each is its own independently-built index (keyed by its own path) that the
        // server federates alongside this project's. Report indexed state, size, and how many OTHER projects
        // share it (so it's clear a shared index outlives an unlink here).
        w.WriteLine("== linked roots ==");
        var links = LinkStore.Read(root);
        if (links.Count == 0) w.WriteLine("    (none)");
        else foreach (var raw in links)
        {
            string linkedRoot;
            try { linkedRoot = Path.GetFullPath(raw); } catch { linkedRoot = raw; }
            var lcache = IndexStore.CacheDirPath(linkedRoot);
            bool exists = Directory.Exists(linkedRoot);
            bool lindexed = SegmentedIndex.Exists(lcache);
            var lmeta = IndexMetaFile.ReadFromCacheDir(lcache);
            int alsoLinkedBy = LinkStore.ProjectsLinking(linkedRoot, excludingProjectRoot: root).Count;
            w.WriteLine($"    {linkedRoot}");
            w.WriteLine($"        exists: {exists}   network: {NetworkPath.IsNetwork(linkedRoot)}   " +
                        (lindexed ? $"indexed: YES ({(lmeta is not null ? $"{lmeta.Files:N0} files, built by {lmeta.Version}" : "no meta")})"
                                  : "indexed: NO (run: codecompass index \"" + linkedRoot + "\")"));
            w.WriteLine($"        shared with {alsoLinkedBy} other project(s)" +
                        (alsoLinkedBy > 0 ? " - its index is reused and survives an unlink here" : ""));
        }

        // Load the index to report live counts (read-only; disposed immediately).
        try
        {
            if (RepositoryIndexer.TryLoad(root, out var text, out var symbols))
            {
                using (text) using (symbols)
                {
                    w.WriteLine($"loads:            YES");
                    w.WriteLine($"documents:        {text.DocumentCount:N0}");
                    w.WriteLine($"text segments:    {text.SegmentCount}");
                    w.WriteLine($"symbol segments:  {symbols.SegmentCount}");
                }
            }
            else w.WriteLine("loads:            NO (no index yet - run: codecompass index)");
        }
        catch (Exception ex) { w.WriteLine($"loads:            ERROR - {ex.GetType().Name}: {ex.Message}"); }
        w.WriteLine();

        // Cache file listing: names + sizes only (names are hashes/segment numbers - not sensitive).
        w.WriteLine("== cache files (names + sizes only; no contents) ==");
        try
        {
            if (Directory.Exists(cacheDir))
            {
                var files = new DirectoryInfo(cacheDir).GetFiles().OrderByDescending(f => f.Length).ToList();
                long total = files.Sum(f => f.Length);
                w.WriteLine($"total:            {Mb(total)} across {files.Count} file(s)");
                foreach (var f in files.Take(60))
                    w.WriteLine($"    {f.Length,12:N0}  {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm}  {f.Name}");
                if (files.Count > 60) w.WriteLine($"    ... and {files.Count - 60} more");
            }
            else w.WriteLine("    (cache dir does not exist)");
        }
        catch (Exception ex) { w.WriteLine($"    (could not list: {ex.Message})"); }
    }

    /// <summary>Run explicit pass/warn health checks for <c>doctor</c>. Never throws.</summary>
    public static IReadOnlyList<Check> HealthChecks(string root)
    {
        root = Path.GetFullPath(root);
        var checks = new List<Check>();

        checks.Add(new("repo directory exists", Directory.Exists(root), root));

        var cacheDir = IndexStore.CacheDirPath(root);
        bool indexed = SegmentedIndex.Exists(cacheDir);
        checks.Add(new("index built", indexed, indexed ? cacheDir : "run: codecompass index \"" + root + "\""));

        if (indexed)
        {
            bool loads = false; string detail;
            try
            {
                if (RepositoryIndexer.TryLoad(root, out var text, out var symbols))
                {
                    using (text) using (symbols) { loads = true; detail = $"{text.DocumentCount:N0} documents, {text.SegmentCount} text segments"; }
                }
                else detail = "TryLoad returned false (cache present but unreadable - will rebuild)";
            }
            catch (Exception ex) { detail = $"{ex.GetType().Name}: {ex.Message}"; }
            checks.Add(new("index loads cleanly", loads, detail));

            var meta = IndexMetaFile.ReadFromCacheDir(cacheDir);
            // Product-version difference is NOT a fault: the version is git-derived and bumps every commit,
            // so almost every upgrade leaves an index "built by an older version" while its CONTENT is
            // byte-identical. Report it as info (Ok), never a warning - warning here is the cry-wolf trap.
            checks.Add(new("index provenance", true,
                meta is null ? "no meta.json (older index - predates version stamping)"
                             : $"built by {meta.Version}, running {BuildInfo.Version}"));

            // The ACTIONABLE staleness signal: was the index built by an indexer whose output logic is behind
            // this binary (so a rebuild would materially change results)? Judged on the content version, which
            // moves only when indexing output changes - so this stays silent across ordinary release upgrades.
            bool behind = IndexMetaFile.IndexerBehind(meta, out int builtCv, out int curCv);
            checks.Add(new("indexer up to date", !behind,
                behind ? $"index built by an older indexer (content v{builtCv} < v{curCv}) - a rebuild would change " +
                         $"results (recall/symbols may be under-reported). Run: codecompass index \"{root}\""
                       : $"content v{curCv} - a rebuild would not change results"));

            // Walk coverage: if the build/update gave up on any directory (unreadable after retry, usually a
            // transient network error), its files are absent - a durable "known-incomplete index" signal so a
            // search/def false-zero is explainable long after the build log has rotated away.
            int dropped = meta?.DroppedDirs ?? 0;
            checks.Add(new("walk coverage complete", dropped == 0,
                dropped == 0 ? "no directories were dropped during the last walk"
                             : $"{dropped:N0} director(y/ies) were unreadable during the last walk - their files are NOT in " +
                               $"the index (search/find_definition may return a false zero). Re-run: codecompass update \"{root}\""));
        }

        if (NetworkPath.IsNetwork(root))
            checks.Add(new("local path (fast metadata)", false, "network share - auto-reconcile off; run 'codecompass update' after external syncs"));

        // Each linked root must exist and be indexed for the server to federate it. A missing/unindexed one
        // is silently absent from results otherwise, so surface it here.
        foreach (var raw in LinkStore.Read(root))
        {
            string linkedRoot;
            try { linkedRoot = Path.GetFullPath(raw); } catch { linkedRoot = raw; }
            if (!Directory.Exists(linkedRoot))
            {
                checks.Add(new($"linked root exists: {linkedRoot}", false, "path not found - unlink it or restore it: codecompass link remove \"" + root + "\" \"" + linkedRoot + "\""));
                continue;
            }
            bool lindexed = SegmentedIndex.Exists(IndexStore.CacheDirPath(linkedRoot));
            checks.Add(new($"linked root indexed: {linkedRoot}", lindexed,
                lindexed ? "" : "not indexed - run: codecompass index \"" + linkedRoot + "\""));
        }

        return checks;
    }
}
