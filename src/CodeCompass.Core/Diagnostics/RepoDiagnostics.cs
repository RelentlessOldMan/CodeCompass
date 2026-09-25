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

    private static readonly HashSet<string> CppExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx", ".c++" };

    // Source translation units to scan for #includes (headers are pulled in transitively; we count TUs).
    private static readonly HashSet<string> CppSourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx", ".c++" };

    // Header extensions that count as "resolvable from the tree" when an #include names one of these.
    private static readonly HashSet<string> CppHeaderExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".h", ".hh", ".hpp", ".hxx", ".h++", ".inc", ".ipp", ".tcc" };

    // #include "path" (group 2, quote form) or #include <path> (group 3, angle form). One per line.
    private static readonly System.Text.RegularExpressions.Regex IncludeLine = new(
        "^[ \\t]*#[ \\t]*include[ \\t]*(?:\"([^\"]+)\"|<([^>]+)>)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    // Common C and C++ standard-library headers. An angle-include of one of these resolves via the toolchain
    // (clang ships/knows them), so it must NOT be flagged missing - otherwise every TU would look broken. Quote
    // includes are resolved against the tree only (they express project-local intent). This list is a heuristic
    // safety net, not exhaustive; the precise per-query find_references disclosure is the authority.
    private static readonly HashSet<string> StdHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        // C
        "assert.h","complex.h","ctype.h","errno.h","fenv.h","float.h","inttypes.h","iso646.h","limits.h",
        "locale.h","math.h","setjmp.h","signal.h","stdalign.h","stdarg.h","stdatomic.h","stdbit.h","stdbool.h",
        "stddef.h","stdint.h","stdio.h","stdlib.h","stdnoreturn.h","string.h","tgmath.h","threads.h","time.h",
        "uchar.h","wchar.h","wctype.h",
        // POSIX / common toolchain headers that live outside the repo tree
        "unistd.h","fcntl.h","sys/types.h","sys/stat.h","sys/time.h","pthread.h","dlfcn.h","sched.h","semaphore.h",
        "arpa/inet.h","netinet/in.h","sys/socket.h","poll.h","dirent.h","malloc.h","alloca.h","endian.h",
    };

    // Honors the configured compileCommands locations (+ CODECOMPASS_COMPILE_COMMANDS), not just the two
    // default probe spots, so a user who points at a DB elsewhere isn't warned as if they had none.
    private static bool HasCompileDb(string root) =>
        CodeCompassConfig.CompileCommandsFiles(root, CodeCompassConfig.ReadFrom(root)).Count > 0;

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
            if (meta.FilesOverCap > 0)
                w.WriteLine($"over size cap:    {meta.FilesOverCap:N0} file(s) NOT indexed (raise maxFileMb / run survey)");
            if (meta.FilesSymbolSkipped > 0)
                w.WriteLine($"symbols skipped:  {meta.FilesSymbolSkipped:N0} file(s) text-searchable but no symbols (over symbol cap / data blob / streamed)");
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

    /// <summary>Outcome of the lexical unresolved-#include scan.</summary>
    public readonly record struct IncludeScan(int TusScanned, int TusWithUnresolved, IReadOnlyList<string> MissingHeaders);

    // Lexical (pre-parse) scan: for each C/C++ source TU, extract its direct #include directives and try to
    // resolve each against the indexed tree (by basename) - quote includes tree-only, angle includes tree +
    // a standard-header/extensionless allowlist. Counts TUs with >=1 unresolvable include and the distinct
    // missing header names. This approximates what clang would fail to find; it does not follow the transitive
    // include graph, so it is a heuristic floor, not the precise per-query truth. Never throws.
    private static IncludeScan ScanUnresolvedIncludes(string root)
    {
        var treeBasenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tus = new List<string>();
        try
        {
            foreach (var f in new FileWalker(new IgnoreRules()).Walk(root))
            {
                treeBasenames.Add(Path.GetFileName(f.RelativePath));
                var ext = Path.GetExtension(f.RelativePath);
                if (CppSourceExtensions.Contains(ext) || CppHeaderExtensions.Contains(ext)) tus.Add(f.FullPath);
            }
        }
        catch { return new IncludeScan(0, 0, Array.Empty<string>()); }

        // Scan the source TUs (the compilation units). Header-only files are pulled in transitively.
        var sources = tus.Where(p => CppSourceExtensions.Contains(Path.GetExtension(p))).ToList();
        int scanned = 0, withUnresolved = 0;
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in sources)
        {
            string text;
            try { text = File.ReadAllText(path); } catch { continue; }
            scanned++;
            bool tuHasUnresolved = false;
            foreach (System.Text.RegularExpressions.Match m in IncludeLine.Matches(text))
            {
                bool quote = m.Groups[1].Success;
                string inc = (quote ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                if (inc.Length == 0) continue;
                string norm = inc.Replace('\\', '/');
                string baseName = Path.GetFileName(norm);
                if (treeBasenames.Contains(baseName)) continue;              // resolvable within the tree
                if (!quote)                                                  // angle include: allow toolchain headers
                {
                    if (!baseName.Contains('.')) continue;                   // <vector>, <cstdint> - extensionless stdlib
                    if (StdHeaders.Contains(baseName) || StdHeaders.Contains(norm)) continue;
                }
                tuHasUnresolved = true;
                missing.Add(baseName);
            }
            if (tuHasUnresolved) withUnresolved++;
        }
        return new IncludeScan(scanned, withUnresolved, missing.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
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

        // C/C++ semantic readiness: find_references for C/C++ resolves precisely only with a compile
        // database. Without one it falls back to best-effort flags and may under-resolve (or resolve
        // nothing), so a repo that has C/C++ sources but no compile_commands.json gets a warning here -
        // otherwise a later "0 C/C++ semantic" reads as "no references" rather than "couldn't run."
        // Single walk of the tree: collects TU count, missing-include stats, and whether any C/C++ exists.
        var incScan = ScanUnresolvedIncludes(root);
        bool hasCpp = incScan.TusScanned > 0;

        if (HasCompileDb(root))
        {
            checks.Add(new("C/C++ compile database", true, "compile_commands.json found (precise C/C++ semantics)"));
        }
        else if (hasCpp)
        {
            checks.Add(new("C/C++ compile database", false,
                "C/C++ sources present but no compile_commands.json found (looked in root, root\\build, and any " +
                "configured compileCommands paths) - find_references uses best-effort flags and may miss references. " +
                "Generate one (CMake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON, or Bear/compiledb for Make), then point at it " +
                "with \"compileCommands\" in .codecompass.json if it's not in a default location"));
        }

        // Unresolvable #includes: a TU that can't find a header often fails to parse, so its references go
        // unresolved. Surface this proactively (find_references also discloses it per-query at lookup time).
        if (hasCpp)
        {
            bool ok = incScan.TusWithUnresolved == 0;
            string detail;
            if (ok)
                detail = $"{incScan.TusScanned:N0} C/C++ translation unit(s) scanned; all direct #includes resolve within the tree";
            else
            {
                var examples = incScan.MissingHeaders.Take(5);
                string tail = incScan.MissingHeaders.Count > 5 ? $", +{incScan.MissingHeaders.Count - 5} more" : "";
                detail = $"{incScan.TusWithUnresolved:N0} of {incScan.TusScanned:N0} C/C++ translation unit(s) reference at least one " +
                         $"#include not found in the tree ({incScan.MissingHeaders.Count:N0} distinct header(s): " +
                         $"{string.Join(", ", examples)}{tail}). These are likely system/vendor headers outside the repo; " +
                         "such TUs may fail to parse, so find_references can under-resolve. Add the missing headers to the tree " +
                         "or point find_references at a compile_commands.json that supplies their include paths. (Heuristic lexical " +
                         "scan of direct includes; the precise gap is disclosed per-query by find_references.)";
            }
            checks.Add(new("C/C++ includes resolvable", ok, detail));
        }

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
