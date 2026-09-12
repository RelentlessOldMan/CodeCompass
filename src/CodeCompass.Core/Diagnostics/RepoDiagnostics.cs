using System.Runtime.InteropServices;
using CodeCompass.Core.Config;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;

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

    /// <summary>Write the full text report. Never throws (best-effort; notes anything it can't read).</summary>
    public static void WriteReport(TextWriter w, string root)
    {
        root = Path.GetFullPath(root);
        w.WriteLine($"CodeCompass diagnostics");
        w.WriteLine($"generated (UTC):  {DateTime.UtcNow:o}");
        w.WriteLine($"version:          {BuildInfo.Version}");
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
            if (k.StartsWith("CODECOMPASS_", StringComparison.OrdinalIgnoreCase)) { w.WriteLine($"    {k}={e.Value}"); anyEnv = true; }
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
            if (!string.Equals(Path.GetFullPath(meta.Root), root, StringComparison.OrdinalIgnoreCase))
                w.WriteLine($"[!] meta root differs: {meta.Root}");
        }
        else w.WriteLine("meta.json:        (none - index predates meta, or not built yet)");

        var status = IndexStatusFile.Read(root);
        if (status is not null) w.WriteLine($"status:           {status.State} - {status.Text}");

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
            bool current = meta is not null && meta.Version == BuildInfo.Version;
            checks.Add(new("built by current version", current,
                meta is null ? "no meta.json (older index)" : $"built by {meta.Version}, running {BuildInfo.Version}"));
        }

        if (NetworkPath.IsNetwork(root))
            checks.Add(new("local path (fast metadata)", false, "network share - auto-reconcile off; run 'codecompass update' after external syncs"));

        return checks;
    }
}
