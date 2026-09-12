using System.Diagnostics;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Hooks;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using CodeCompass.Core.Text;
using CodeCompass.Core.Walking;
using CodeCompass.Semantics;

ProcessPerformance.RequestFullSpeed(); // opt out of EcoQoS so `index`/`update` run at full speed

// Record the version + argv on every run so a user's log pins the exact build behind any report.
// (Hook subcommands stay silent - their stdout is a protocol channel Claude Code parses.)
if (args.Length > 0 && args[0] is not ("hook-block" or "hook-context"))
    Log.Global.Info($"cli v{BuildInfo.Version}: {string.Join(' ', args)}");

return args.Length == 0
    ? Usage()
    : args[0].ToLowerInvariant() switch
    {
        "index" => CmdIndex(args),
        "update" => CmdUpdate(args),
        "search" => CmdSearch(args),
        "def" => CmdDef(args),
        "symbols" => CmdSymbols(args),
        "refs" => CmdRefs(args),
        "watch" => CmdWatch(args),
        "survey" => CmdSurvey(args),
        "init" => CmdInit(args),
        "symstats" => CmdSymStats(args),
        "parsebench" => CmdParseBench(args),
        "statusline" => CmdStatusline(args),
        "logs" => CmdLogs(),
        "version" or "--version" or "-v" => CmdVersion(),
        "hook-block" => CmdHookBlock(),     // PreToolUse hook: deny Grep/Glob
        "hook-context" => CmdHookContext(), // SessionStart hook: inject guidance
        _ => Usage(),
    };

// Print the build version (e.g. 1.0.123+a1b2c3d4). Users quote this in bug reports so a
// problem can be pinned to an exact commit.
static int CmdVersion()
{
    Console.WriteLine($"CodeCompass {BuildInfo.Version}");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("CodeCompass (Phase 5)");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  codecompass index   <path>               (re)build the full index");
    Console.Error.WriteLine("  codecompass update  <path>               incremental reindex of changes");
    Console.Error.WriteLine("  codecompass watch   <path>               auto-reindex on file changes");
    Console.Error.WriteLine("  codecompass search  <path> <query>       literal text search");
    Console.Error.WriteLine("  codecompass def     <path> <name>        exact symbol definition(s)");
    Console.Error.WriteLine("  codecompass symbols <path> <substring>   symbol name search");
    Console.Error.WriteLine("  codecompass refs    <path> <name>        references (semantic C#/C++, lexical elsewhere)");
    Console.Error.WriteLine("  codecompass survey  <path>               report what the size caps skip + suggest config");
    Console.Error.WriteLine("  codecompass init    <path>               write a documented .codecompass.json (per-repo settings)");
    Console.Error.WriteLine("  codecompass symstats <path> [--full]     profile symbol-file sizes + parse cost per language");
    Console.Error.WriteLine("  codecompass parsebench                   tree-sitter parse-time vs size sweep (synthetic)");
    Console.Error.WriteLine("  codecompass statusline [--wrap \"<cmd>\"]  Claude Code status line: shows index state (reads stdin JSON)");
    Console.Error.WriteLine("  codecompass logs                         show the log folder and files");
    Console.Error.WriteLine("  codecompass version                      print the build version");
    return 1;
}

// Report what the current caps would skip, so an operator can decide whether to raise them
// (in .codecompass.json or via env). Never changes anything.
static int CmdSurvey(string[] args)
{
    if (args.Length < 2) return Usage();
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"not a directory: {root}"); return 1; }

    var r = Surveyor.Survey(root);
    static double Mb(long b) => b / 1048576.0;

    Console.WriteLine($"Indexed:     {r.IndexedFiles:N0} files ({Mb(r.IndexedBytes):F0} MB)");
    Console.WriteLine($"Symbol cap:  {Mb(r.MaxSymbolBytes):F0} MB   (maxSymbolMb / CODECOMPASS_MAX_SYMBOL_MB)");
    Console.WriteLine($"File cap:    {Mb(r.MaxFileBytes):F0} MB   (maxFileMb / CODECOMPASS_MAX_FILE_MB)");

    Console.WriteLine();
    if (r.SymbolSkipped.Count == 0)
    {
        Console.WriteLine("No files over the symbol cap - every eligible file gets go-to-definition.");
    }
    else
    {
        Console.WriteLine($"{r.SymbolSkipped.Count:N0} file(s) over the symbol cap - text-searchable but NO go-to-definition:");
        foreach (var (p, b) in r.SymbolSkipped.Take(5)) Console.WriteLine($"    {Mb(b),6:F1} MB  {p}");
        if (r.SymbolSkipped.Count > 5) Console.WriteLine($"    ... and {r.SymbolSkipped.Count - 5:N0} more");
        int suggest = (int)Math.Ceiling(Mb(r.SymbolSkipped[0].Bytes));
        Console.WriteLine($"  If these are valid code whose symbols you want, set \"maxSymbolMb\": {suggest} in .codecompass.json.");
        Console.WriteLine("  Raising it is safe: above 1 MB, files that are overwhelmingly numeric/hex data (generated");
        Console.WriteLine("  arrays - slow to parse, zero symbols) are auto-skipped by content, so only real code is parsed.");
        Console.WriteLine("  Run 'symstats'/'parsebench' first to see sizes and parse cost.");
    }

    Console.WriteLine();
    if (r.OverFileCap.Count == 0)
    {
        Console.WriteLine("No files over the file cap - nothing is excluded from search by size.");
    }
    else
    {
        Console.WriteLine($"{r.OverFileCap.Count:N0} file(s) over the file cap - absent from the index (not searchable):");
        foreach (var (p, b) in r.OverFileCap.Take(5)) Console.WriteLine($"    {Mb(b),6:F1} MB  {p}");
        if (r.OverFileCap.Count > 5) Console.WriteLine($"    ... and {r.OverFileCap.Count - 5:N0} more");
        int suggest = (int)Math.Ceiling(Mb(r.OverFileCap[0].Bytes));
        Console.WriteLine($"  To include them in text search, set \"maxFileMb\": {suggest} in .codecompass.json.");
    }
    return 0;
}

// Write a documented .codecompass.json at the repo root so the user has a starting point they can edit,
// instead of guessing the field names. Every setting is commented out (defaults apply until edited).
static int CmdInit(string[] args)
{
    if (args.Length < 2) return Usage();
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"not a directory: {root}"); return 1; }

    var path = Path.Combine(root, CodeCompass.Core.Config.CodeCompassConfig.FileName);
    if (File.Exists(path)) { Console.Error.WriteLine($"{path} already exists - not overwriting."); return 1; }

    File.WriteAllText(path, CodeCompass.Core.Config.CodeCompassConfig.Template);
    Console.WriteLine($"wrote {path}");
    Console.Error.WriteLine("Every setting is commented out (defaults apply). Uncomment only what you want to change.");
    return 0;
}

// Profile a real repo: how big files get per language (free - stat only), whether the big ones yield
// symbols, and where parse cost starts to hurt. The data behind a maxSymbolMb choice. Safe on huge
// trees: the size distribution covers all files without parsing; tree-sitter runs only on the largest
// N files per language (--full to parse all), sequentially, timeout-guarded - it can't hang or crawl.
static int CmdSymStats(string[] args)
{
    bool full = args.Any(a => a is "--full" or "-full");
    var pathArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
    if (pathArg is null) return Usage();
    var root = Path.GetFullPath(pathArg);
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"not a directory: {root}"); return 1; }

    static string Sz(long b) =>
        b >= 1048576 ? $"{b / 1048576.0,6:F1} MB" : $"{b / 1024.0,6:F1} KB";

    Console.Error.Write(full ? "profiling (parsing every source file)..." : "profiling (sizes for all files; parsing the largest per language)...");
    var r = SymbolProfiler.Profile(root, full: full);
    Console.Error.Write("\r" + new string(' ', 72) + "\r");

    string scope = r.Full ? "parsed ALL files" : $"parsed the largest {r.MaxParsePerLang}/language (--full for all)";
    Console.WriteLine($"Scope: sizes from all files (no parse); {scope}. Per-file timeout {r.TimeoutMs / 1000.0:F0}s.");
    Console.WriteLine();
    Console.WriteLine($"{"ext",-6} {"lang",-11} {"files",7}  {"file size p50/p95/max",-26}  {"parsed",6} {"w/syms",6}  slowest parse");
    foreach (var p in r.Languages)
    {
        string cand = $"{Sz(p.CandidateP50)} /{Sz(p.CandidateP95)} /{Sz(p.CandidateMax)}";
        Console.WriteLine($"{p.Extension,-6} {p.Language,-11} {p.CandidateFiles,7}  {cand,-26}  {p.FilesParsed,6} {p.FilesWithSymbols,6}  {p.SlowestMs,6:F0} ms @ {Sz(p.SlowestBytes)}");
        Console.WriteLine($"         largest file: {p.CandidateMaxPath}");
        if (p.FilesWithSymbols > 0)
            Console.WriteLine($"         largest parsed file WITH symbols: {Sz(p.LargestSymbolBearing)}  {p.LargestSymbolBearingPath}");
        if (p.Knee is not null)
        {
            var k = p.Knee;
            string how = k.TimedOut ? "TIMED OUT" : $"{k.ParseMs:F0} ms";
            Console.WriteLine($"         [!] parse cost knee: {Sz(k.Bytes)} took {how} ({k.Path}) - larger files not parsed");
        }
    }

    // The headline: the two numbers that bracket a good cap.
    long maxSym = r.Languages.Count > 0 ? r.Languages.Max(p => p.LargestSymbolBearing) : 0;
    var firstSlow = r.All.Where(p => p.TimedOut || p.ParseMs >= 1000).OrderBy(p => p.Bytes).FirstOrDefault();
    Console.WriteLine();
    Console.WriteLine($"Largest file that actually produced symbols: {Sz(maxSym)}");
    if (firstSlow is not null)
        Console.WriteLine($"Smallest file whose parse got slow (>=1s):   {Sz(firstSlow.Bytes)}  ({firstSlow.Path})");
    else
        Console.WriteLine("No parsed file's parse reached 1s - tree-sitter is comfortable on the files sampled.");
    Console.WriteLine("Set maxSymbolMb above the first line to cover real symbols; keep it below the second so a");
    Console.WriteLine("slow-to-parse file can't stall indexing. (Run 'parsebench' to see the parse-cost curve.)");
    if (r.BudgetExhausted)
        Console.WriteLine("NOTE: hit the wall-clock budget - some large files went unparsed. Re-run with --full for complete coverage.");
    return 0;
}

// Synthetic tree-sitter parse-cost sweep: doubling file sizes, ordinary code vs the pathological
// header shape, to show where cost goes quadratic. No repo needed - deterministic characterization.
static int CmdParseBench(string[] args)
{
    var exts = args.Skip(1).Where(a => a.StartsWith('.')).ToArray();
    static string Sz(long b) => b >= 1048576 ? $"{b / 1048576.0,5:F0} MB" : $"{b / 1024.0,5:F0} KB";

    Console.Error.Write("sweeping (this parses progressively larger synthetic files)...");
    var series = ParseSweep.Run(extensions: exts.Length > 0 ? exts : null);
    Console.Error.Write("\r" + new string(' ', 64) + "\r");

    foreach (var s in series)
    {
        Console.WriteLine();
        Console.WriteLine($"{s.Extension} / {s.Shape}   (growth ~2x per row = linear, ~4x = quadratic)");
        Console.WriteLine($"  {"size",8}  {"parse",10}  {"MB/s",7}  {"growth",7}  symbols");
        foreach (var pt in s.Points)
        {
            string mbps = pt.ParseMs > 0 ? $"{pt.Bytes / 1048576.0 / (pt.ParseMs / 1000.0),7:F1}" : "      -";
            string growth = pt.GrowthFactor > 0 ? $"{pt.GrowthFactor,6:F1}x" : "      -";
            string parse = pt.TimedOut ? "TIMED OUT" : $"{pt.ParseMs,8:F0} ms";
            string syms = pt.Symbols < 0 ? "err" : pt.Symbols.ToString("N0");
            Console.WriteLine($"  {Sz(pt.Bytes),8}  {parse,10}  {mbps}  {growth}  {syms}");
        }
    }
    // Summary: steady-state throughput per shape (from the largest completed point), slowest first,
    // with the projected parse time at the current symbol cap. This is the actionable read.
    long capBytes = CodeCompass.Core.Config.CodeCompassConfig.MaxSymbolChars();
    Console.WriteLine();
    Console.WriteLine("Summary - effective throughput (linear across sizes; the constant is what varies):");
    Console.WriteLine($"  {"shape",-16} {"ext",-5} {"MB/s",7}   projected parse @ {capBytes / 1048576.0:F0} MB cap");
    var rows = series
        .Select(s =>
        {
            var last = s.Points.LastOrDefault(p => !p.TimedOut && p.ParseMs > 0);
            double mbps = last is not null ? last.Bytes / 1048576.0 / (last.ParseMs / 1000.0) : 0;
            return (s.Shape, s.Extension, Mbps: mbps, AnyTimeout: s.Points.Any(p => p.TimedOut));
        })
        .OrderBy(r => r.Mbps == 0 ? double.MaxValue : r.Mbps);
    foreach (var r in rows)
    {
        string atCap = r.Mbps > 0 ? $"~{capBytes / 1048576.0 / r.Mbps:F1}s" : "n/a";
        string flag = r.AnyTimeout ? "  (timed out at a larger size)" : "";
        Console.WriteLine($"  {r.Shape,-16} {r.Extension,-5} {r.Mbps,7:F2}   {atCap}{flag}");
    }
    Console.WriteLine();
    Console.WriteLine("Growth stays ~2x per doubling (linear) - no quadratic explosion on this grammar. The risk");
    Console.WriteLine("is a slow constant x a large file; the symbol cap bounds per-file parse time regardless.");
    return 0;
}

// Claude Code status-line command. Claude runs this on every render, piping a small JSON object on
// stdin (session_id, cwd, workspace.project_dir, model, ...). We read the repo's status file (written
// by the MCP server) and print a compact segment like "CodeCompass ✓ 48,000 files". A separate
// short-lived process can't see the server's memory, so the status is bridged through that file.
//
// --wrap "<cmd>": compose with an existing status line. We forward the SAME stdin JSON to <cmd>, print
// its output, then append " | <our segment>". Claude Code has a single status-line slot, so this lets
// a user keep their existing status line and still see CodeCompass.
//
// Contract: fast, silent on any error, and prints NOTHING (for our segment) when there's no status
// file - so it's harmless in repos CodeCompass has never indexed.
static int CmdStatusline(string[] args)
{
    // Claude Code reads this line as UTF-8; on Windows the console defaults to a legacy codepage that
    // would mangle the status glyphs (✓ ↻ …). Force UTF-8 (best-effort - can throw if redirected oddly).
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

    string stdin = "";
    try { stdin = Console.In.ReadToEnd(); } catch { /* no stdin piped */ }

    // --wrap: run the user's existing status-line command first, forwarding the same stdin.
    string? wrapped = null;
    int wi = Array.FindIndex(args, a => a is "--wrap" or "-wrap");
    if (wi >= 0 && wi + 1 < args.Length)
    {
        try { wrapped = RunWrapped(args[wi + 1], stdin)?.TrimEnd('\r', '\n'); }
        catch { wrapped = null; }
    }

    string segment = "";
    try
    {
        var root = RepoRootFromStatuslineJson(stdin);
        if (root is not null)
        {
            var status = CodeCompass.Core.Storage.IndexStatusFile.Read(root);
            if (status is not null) segment = $"CodeCompass {StatusIcon(status.State)} {status.Text}";
        }
    }
    catch { /* status line must never fail loudly */ }

    // Compose: wrapped output, then our segment (only when we have one).
    string line = (wrapped, segment) switch
    {
        ({ Length: > 0 }, { Length: > 0 }) => $"{wrapped} | {segment}",
        ({ Length: > 0 }, _) => wrapped!,
        (_, { Length: > 0 }) => segment,
        _ => "",
    };
    if (line.Length > 0) Console.WriteLine(line);
    return 0;
}

static string StatusIcon(string state) => state switch
{
    "ready" => "✓",         // ✓
    "building" => "…",      // …
    "reconciling" => "↻",   // ↻
    "needsCliBuild" => "⚠", // ⚠
    _ => "•",               // •
};

// Determine the repo root Claude is in from the status-line stdin JSON. Prefer the workspace project
// dir (repo root), fall back to the current dir, then this process's cwd.
static string? RepoRootFromStatuslineJson(string json)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("workspace", out var ws))
            {
                if (ws.TryGetProperty("project_dir", out var pd) && pd.ValueKind == System.Text.Json.JsonValueKind.String)
                    return Path.GetFullPath(pd.GetString()!);
                if (ws.TryGetProperty("current_dir", out var cd) && cd.ValueKind == System.Text.Json.JsonValueKind.String)
                    return Path.GetFullPath(cd.GetString()!);
            }
            if (r.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == System.Text.Json.JsonValueKind.String)
                return Path.GetFullPath(cwd.GetString()!);
        }
    }
    catch { /* not the expected shape */ }
    return null;
}

// Run a user-supplied status-line command through the OS shell, forwarding Claude's stdin JSON, and
// return its stdout. Best-effort with a short timeout so a slow/hung wrapped command can't stall the
// whole status line.
static string? RunWrapped(string command, string stdin)
{
    var psi = new ProcessStartInfo
    {
        FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    if (OperatingSystem.IsWindows()) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
    else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }

    using var p = Process.Start(psi);
    if (p is null) return null;
    try { p.StandardInput.Write(stdin); } catch { }
    finally { try { p.StandardInput.Close(); } catch { } }
    string outText = p.StandardOutput.ReadToEnd();
    if (!p.WaitForExit(2000)) { try { p.Kill(entireProcessTree: true); } catch { } return null; }
    return outText;
}

// Print the central log location and current log files - the one place to look when debugging.
static int CmdLogs()
{
    var dir = Log.Directory;
    Console.WriteLine(dir);
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine("(no logs yet)");
        return 0;
    }
    foreach (var f in new DirectoryInfo(dir).GetFiles("*.log*").OrderByDescending(f => f.LastWriteTime))
        Console.WriteLine($"  {f.Length / 1024.0,8:F0} KB  {f.LastWriteTime:yyyy-MM-dd HH:mm}  {f.Name}");
    var lvl = Environment.GetEnvironmentVariable("CODECOMPASS_LOG_LEVEL");
    Console.Error.WriteLine($"-- level: {(string.IsNullOrWhiteSpace(lvl) ? "info (default)" : lvl)} " +
                            "(set CODECOMPASS_LOG_LEVEL=debug|info|warn|error|off)");
    return 0;
}

static int CmdIndex(string[] args)
{
    if (args.Length < 2) return Usage();
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"not a directory: {root}");
        return 1;
    }

    // Percent/ETA needs a total, which means an up-front walk of the whole tree. On a network share
    // that second walk doubles the (slow) metadata round-trips and shows a dead "scanning tree..."
    // line for minutes - so skip the pre-count for network roots and show indeterminate progress
    // instead. Locally the pre-count is ~free, and we heartbeat it so it never looks frozen.
    bool network = IsNetworkPath(root);
    int totalFiles = 0;
    long totalBytes = 0;
    bool haveTotals = false;
    if (network)
    {
        Console.Error.WriteLine("indexing a network path: skipping the pre-scan (no %/ETA) to halve round-trips - this is a one-time full read.");
    }
    else
    {
        Console.Error.Write("scanning tree...");
        var scanSw = Stopwatch.StartNew();
        foreach (var f in new FileWalker(new IgnoreRules()).Walk(root))
        {
            totalFiles++; totalBytes += f.Size;
            if (scanSw.ElapsedMilliseconds >= 500) { Console.Error.Write($"\rscanning tree... {totalFiles:N0} files   "); scanSw.Restart(); }
        }
        Console.Error.Write("\r" + new string(' ', 40) + "\r");
        haveTotals = true;
    }

    var progressSw = Stopwatch.StartNew();
    void Progress(int files, long bytes)
    {
        double el = progressSw.Elapsed.TotalSeconds;
        double mbps = el > 0 ? bytes / 1048576.0 / el : 0;
        if (haveTotals)
        {
            double pct = totalBytes > 0 ? 100.0 * bytes / totalBytes : 0;
            double eta = mbps > 0 ? (totalBytes - bytes) / 1048576.0 / mbps : 0;
            Console.Error.Write($"\rindexing {pct,5:F1}%  {files:N0}/{totalFiles:N0} files  " +
                                $"{bytes / 1073741824.0:F2}/{totalBytes / 1073741824.0:F2} GB  {mbps:F0} MB/s  ETA {FormatEta(eta)}   ");
        }
        else
        {
            Console.Error.Write($"\rindexing  {files:N0} files  {bytes / 1073741824.0:F2} GB  {mbps:F0} MB/s   ");
        }
    }

    Log.For(root).Info($"cli index started ({totalFiles:N0} files, {totalBytes / 1048576.0:F0} MB)");
    IndexStats s;
    try
    {
        var (ti, sy, st) = RepositoryIndexer.Build(root, Progress);
        ti.Dispose();
        sy.Dispose();
        s = st;
    }
    catch (Exception ex)
    {
        Log.For(root).Error("cli index failed", ex);
        Console.Error.WriteLine($"\rindex failed: {ex.Message}");
        return 1;
    }
    Console.Error.Write("\r" + new string(' ', 90) + "\r");
    Log.For(root).Info($"cli index complete: {s.Files:N0} files in {s.Seconds:F1}s ({(s.Seconds > 0 ? s.Bytes / 1048576.0 / s.Seconds : 0):F1} MB/s)");

    double mb = s.Bytes / (1024.0 * 1024.0);
    double throughput = s.Seconds > 0 ? mb / s.Seconds : 0;
    double perCore = s.Cores > 0 ? throughput / s.Cores : throughput;
    double ratio = s.Bytes > 0 ? (double)s.IndexBytes / s.Bytes : 0;

    Console.WriteLine($"Indexed {s.Files:N0} files ({mb:F1} MB) in {s.Seconds:F2}s");
    Console.WriteLine($"Throughput: {throughput:F1} MB/s across {s.Cores} core(s)  ({perCore:F1} MB/s/core)");
    Console.WriteLine($"Trigram postings: {s.TrigramPostings:N0}   Symbols: {s.Symbols:N0}");
    Console.WriteLine($"Text index: {s.IndexBytes / (1024.0 * 1024.0):F1} MB ({ratio:F2}x corpus)");
    PublishReady(root, (int)s.Files); // so the status line shows "ready" even before the MCP server loads
    return 0;
}

// Publish a "ready" status file so `codecompass statusline` reflects a freshly CLI-built index even
// before the MCP server's first tool call loads it. Respects the same config gate as the server.
static void PublishReady(string root, int files)
{
    CodeCompass.Core.Config.CodeCompassConfig.Load(root); // honor a repo's statusLine:false
    if (!CodeCompass.Core.Config.CodeCompassConfig.StatusLinePublish()) return;
    CodeCompass.Core.Storage.IndexStatusFile.Write(root,
        new CodeCompass.Core.Storage.IndexStatus("ready", $"{files:N0} files", files));
}

static int CmdUpdate(string[] args)
{
    if (args.Length < 2) return Usage();
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"not a directory: {root}"); return 1; }

    // The change-detection pass stat-walks the whole tree silently; over a network share that's
    // minutes of blank console. Heartbeat it so it doesn't read as a hang.
    Console.Error.Write("scanning for changes...");
    var scanSw = Stopwatch.StartNew();
    void OnScan(int n) { if (scanSw.ElapsedMilliseconds >= 500) { Console.Error.Write($"\rscanning for changes... {n:N0} files   "); scanSw.Restart(); } }

    var (idx, _, s) = RepositoryIndexer.Update(root, OnScan);
    idx.Dispose();
    Console.Error.Write("\r" + new string(' ', 40) + "\r");
    if (s.FullRebuild)
    {
        Console.WriteLine($"Full rebuild ({s.Added} files) in {s.Seconds:F2}s");
        Log.For(root).Info($"cli update -> full rebuild ({s.Added} files) in {s.Seconds:F1}s");
    }
    else
    {
        Console.WriteLine($"Updated in {s.Seconds:F2}s: +{s.Added} added, ~{s.Modified} modified, -{s.Removed} removed");
        Log.For(root).Info($"cli update: +{s.Added} ~{s.Modified} -{s.Removed} in {s.Seconds:F1}s");
    }
    if (RepositoryIndexer.TryLoad(root, out var reloaded, out _)) { PublishReady(root, reloaded.DocumentCount); reloaded.Dispose(); }
    return 0;
}

static int CmdWatch(string[] args)
{
    if (args.Length < 2) return Usage();
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"not a directory: {root}"); return 1; }

    if (IsNetworkPath(root))
        Console.Error.WriteLine("note: watching a network path - FileSystemWatcher change events are unreliable over SMB, " +
                                "so edits may be missed. Run 'codecompass update' after a big external change (e.g. a source-control sync).");

    SegmentedIndex text;
    SegmentedSymbolIndex symbols;
    if (RepositoryIndexer.TryLoad(root, out text, out symbols))
    {
        Console.Error.WriteLine("loaded existing index");
    }
    else
    {
        Console.Error.WriteLine("building initial index...");
        var b = RepositoryIndexer.Build(root);
        text = b.Text;
        symbols = b.Symbols;
        Console.Error.WriteLine($"indexed {b.Stats.Files} files");
    }
    var snapshot = RepositoryIndexer.LoadSnapshot(root);

    // Keep the index in memory and apply targeted changes in place (no reopen per batch).
    using var watcher = new RepositoryWatcher(root, batch =>
    {
        try
        {
            if (batch.FullReconcile)
            {
                text.Dispose();
                symbols.Dispose();
                snapshot.Dispose();
                var b = RepositoryIndexer.Build(root);
                text = b.Text;
                symbols = b.Symbols;
                snapshot = RepositoryIndexer.LoadSnapshot(root);
                Console.Error.WriteLine("reindexed: full rebuild");
                Log.For(root).Info("watch: full rebuild");
            }
            else
            {
                var c = RepositoryIndexer.ApplyChanges(text, symbols, snapshot, root, batch.ChangedFullPaths);
                RepositoryIndexer.Persist(root, text, symbols, snapshot);
                if (c.Added != 0 || c.Modified != 0 || c.Removed != 0)
                {
                    Console.Error.WriteLine($"reindexed: +{c.Added} ~{c.Modified} -{c.Removed}");
                    Log.For(root).Info($"watch reindex: +{c.Added} ~{c.Modified} -{c.Removed}");
                }

                // Keep a long-running watch from accumulating unbounded segments/tombstones.
                // Merge existing segments in place (no source-file re-read).
                if (RepositoryIndexer.NeedsCompaction(text, symbols))
                {
                    text.Compact();
                    symbols.Compact();
                    Console.Error.WriteLine("compacted: merged segments");
                    Log.For(root).Info("watch: compacted (merged segments)");
                }
            }
        }
        catch (Exception ex)
        {
            Log.For(root).Error("watch reindex failed", ex);
        }
    });
    watcher.Start();

    Log.For(root).Info("cli watch started");
    Console.Error.WriteLine($"watching {root} - press Ctrl+C to stop");
    using var exit = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
    exit.Wait();
    watcher.Dispose(); // stop the watcher and drain any in-flight reindex BEFORE freeing the indexes it touches
    text.Dispose();
    symbols.Dispose();
    snapshot.Dispose();
    return 0;
}

static int CmdSearch(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var query = string.Join(' ', args.Skip(2));

    if (!RepositoryIndexer.TryLoad(root, out var index, out _)) return NoIndex(root);

    const int cap = 200;
    var sw = Stopwatch.StartNew();
    var matches = index.Search(query, cap + 1); // one extra to detect truncation
    sw.Stop();

    bool truncated = matches.Count > cap;
    foreach (var m in matches.Take(cap))
        Console.WriteLine($"{m.Path}:{m.Line}:{m.Column}: {m.LineText}");
    Console.Error.WriteLine(truncated
        ? $"-- showing first {cap}; MORE EXIST (narrow the query) in {sw.Elapsed.TotalMilliseconds:F0} ms"
        : $"-- {matches.Count} match(es) in {sw.Elapsed.TotalMilliseconds:F0} ms");
    return 0;
}

static int CmdDef(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var name = args[2];

    if (!RepositoryIndexer.TryLoad(root, out _, out var symbols)) return NoIndex(root);

    var matches = symbols.FindByName(name);
    foreach (var symbol in matches)
        Console.WriteLine($"{symbol.RelativePath}:{symbol.Line}:{symbol.Column}: {symbol.Kind} {symbol.Name}");
    Console.Error.WriteLine($"-- {matches.Count} definition(s)");
    return 0;
}

static int CmdSymbols(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var query = args[2];

    if (!RepositoryIndexer.TryLoad(root, out _, out var symbols)) return NoIndex(root);

    var matches = symbols.Find(query);
    foreach (var symbol in matches)
        Console.WriteLine($"{symbol.RelativePath}:{symbol.Line}:{symbol.Column}: {symbol.Kind} {symbol.Name}");
    Console.Error.WriteLine($"-- {matches.Count} symbol(s)");
    return 0;
}

static int CmdRefs(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var name = args[2];

    // Precise semantic references (comments/strings excluded). Note: from the CLI these
    // build fresh each run; the MCP server keeps them warm across calls.
    var cs = new RoslynCSharpAnalyzer(root).FindReferences(name);
    foreach (var s in cs)
        Console.WriteLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.LineText}");

    var cpp = new ClangCppAnalyzer(root).FindReferences(name);
    foreach (var s in cpp)
        Console.WriteLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.LineText}");

    // Lexical whole-word references for languages without a semantic analyzer.
    int lexical = 0;
    if (RepositoryIndexer.TryLoad(root, out var index, out _))
    {
        foreach (var m in index.Search(name, 1000))
        {
            if (SemanticCoverage.IsCovered(m.Path)) continue;
            if (!WordBoundary.IsWholeWord(m.LineText, m.Column - 1, name.Length)) continue;
            Console.WriteLine($"{m.Path}:{m.Line}:{m.Column}: {m.LineText}");
            lexical++;
        }
    }

    Console.Error.WriteLine($"-- {cs.Count} C# + {cpp.Count} C/C++ semantic + {lexical} lexical reference(s)");
    return 0;
}

// PreToolUse hook: block Grep/Glob and redirect the agent to CodeCompass.
// The plugin's matcher already limits this to Grep|Glob, so we always deny when
// enforcement is on. Set CODECOMPASS_ENFORCE=0 to disable (grep fallback).
static int CmdHookBlock()
{
    try { _ = Console.In.ReadToEnd(); } catch { /* consume hook stdin */ }

    var enforce = Environment.GetEnvironmentVariable("CODECOMPASS_ENFORCE");
    if (enforce is "0" || string.Equals(enforce, "off", StringComparison.OrdinalIgnoreCase))
        return 0; // enforcement disabled: let the normal permission flow proceed

    Console.WriteLine(HookPayloads.DenySearch());
    return 0;
}

// SessionStart hook: tell the agent to prefer CodeCompass for search/navigation.
static int CmdHookContext()
{
    try { _ = Console.In.ReadToEnd(); } catch { /* consume hook stdin */ }

    Console.WriteLine(HookPayloads.SessionContext());
    return 0;
}

static bool IsNetworkPath(string fullPath) => CodeCompass.Core.Storage.NetworkPath.IsNetwork(fullPath);

static string FormatEta(double seconds)
{
    if (seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "--";
    var t = TimeSpan.FromSeconds(seconds);
    if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:D2}m";
    if (t.TotalMinutes >= 1) return $"{t.Minutes}m{t.Seconds:D2}s";
    return $"{t.Seconds}s";
}

static int NoIndex(string root)
{
    Console.Error.WriteLine($"no index for {root}");
    Console.Error.WriteLine($"run: codecompass index \"{root}\"");
    return 1;
}
