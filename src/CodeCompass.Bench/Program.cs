using System.Text.Json;
using CodeCompass.Bench;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Text;
using CodeCompass.Core.Walking;

ProcessPerformance.RequestFullSpeed(); // opt out of EcoQoS so bench numbers reflect full-speed indexing

if (args.Length == 0) return Usage();

switch (args[0].ToLowerInvariant())
{
    case "list": return CmdList();
    case "fetch": return await CmdFetch(args);
    case "run": return CmdRun(args);
    case "bench": return await CmdBench(args);
    case "verify": return await CmdVerify(args);
    case "symbols": return await CmdSymbols(args);
    case "eval": return CmdEval(args);
    case "all": return CmdAll(args);
    default: return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("CodeCompass benchmark harness");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  bench list                     list corpora in the manifest");
    Console.Error.WriteLine("  bench fetch <id|tier|all>      download+extract pinned corpora to .corpus/");
    Console.Error.WriteLine("  bench run    <path>            benchmark a local directory");
    Console.Error.WriteLine("  bench bench  <id>              fetch (if needed) then benchmark a manifest corpus");
    Console.Error.WriteLine("  bench verify <path|id|tier> [budgetMB] [queries]");
    Console.Error.WriteLine("                                 correctness: trigram search vs brute-force on real files");
    Console.Error.WriteLine("  bench symbols <path|id>        symbol-extraction smoke: per-language symbol counts (fails on zero)");
    Console.Error.WriteLine("  bench eval   <path> [sampleSize]  estimate tokens saved: find_definition vs grep+read");
    Console.Error.WriteLine("  bench all    [all|tier|id]     perf + correctness over fetched corpora -> HTML report");
    Console.Error.WriteLine();
    Console.Error.WriteLine("env: CODECOMPASS_MANIFEST (manifest path), CODECOMPASS_CORPUS_DIR (cache dir)");
    return 1;
}

static int CmdEval(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: bench eval <path> [sampleSize]"); return 1; }
    var path = args[1];
    if (!Directory.Exists(path)) { Console.Error.WriteLine($"not a directory: {path}"); return 1; }
    int sample = args.Length > 2 && int.TryParse(args[2], out var n) && n > 0 ? n : 50;
    TokenEval.Run(Path.GetFullPath(path), sample);
    return 0;
}

static string ManifestPath() =>
    Environment.GetEnvironmentVariable("CODECOMPASS_MANIFEST")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "bench", "corpus-manifest.json");

static int CmdList()
{
    var path = ManifestPath();
    if (!File.Exists(path)) { Console.Error.WriteLine($"manifest not found: {path}"); return 1; }
    var manifest = CorpusManifest.Load(path);
    foreach (var group in manifest.Corpora.GroupBy(c => c.Tier))
    {
        Console.WriteLine($"[{group.Key}]");
        foreach (var c in group)
            Console.WriteLine($"  {c.Id,-12} {c.Language,-12} {c.Note}");
    }
    return 0;
}

static async Task<int> CmdFetch(string[] args)
{
    if (args.Length < 2) return Usage();
    var manifest = CorpusManifest.Load(ManifestPath());
    var selector = args[1];

    var targets = manifest.Corpora.Where(c =>
        selector == "all" || c.Id == selector || c.Tier == selector).ToList();

    if (targets.Count == 0) { Console.Error.WriteLine($"no corpora match '{selector}'"); return 1; }

    foreach (var entry in targets)
    {
        var root = await CorpusFetcher.FetchAsync(entry);
        Console.WriteLine($"{entry.Id}: {root}");
    }
    return 0;
}

static int CmdRun(string[] args)
{
    if (args.Length < 2) return Usage();
    var path = Path.GetFullPath(args[1]);
    if (!Directory.Exists(path)) { Console.Error.WriteLine($"not a directory: {path}"); return 1; }
    return RunAndReport(path);
}

static async Task<int> CmdBench(string[] args)
{
    if (args.Length < 2) return Usage();
    var manifest = CorpusManifest.Load(ManifestPath());
    var entry = manifest.Corpora.FirstOrDefault(c => c.Id == args[1]);
    if (entry is null) { Console.Error.WriteLine($"unknown corpus '{args[1]}'"); return 1; }

    var root = await CorpusFetcher.FetchAsync(entry);
    return RunAndReport(root);
}

static async Task<int> CmdVerify(string[] args)
{
    if (args.Length < 2) return Usage();

    long budgetMb = args.Length > 2 && long.TryParse(args[2], out var b) ? b : 100;
    int queries = args.Length > 3 && int.TryParse(args[3], out var q) ? q : 40;

    var paths = await ResolveTargets(args[1]); // a corpus id, a whole tier, or a local path
    if (paths is null) return 1;

    int failures = 0;
    foreach (var path in paths)
    {
        var r = Verifier.LexicalOracle(path, budgetMb * 1024 * 1024, queries);
        Console.WriteLine();
        Console.WriteLine($"[{Path.GetFileName(path)}] lexical oracle: {r.Queries} queries over {r.SubsetFiles:N0} files " +
                          $"({r.SubsetBytes / (1024.0 * 1024.0):N1} MB verified)");
        if (r.Mismatches == 0)
            Console.WriteLine("  PASS - trigram search matches the brute-force scan exactly.");
        else
        {
            failures++;
            Console.WriteLine($"  FAIL - {r.Mismatches} mismatch(es):");
            foreach (var ex in r.Examples) Console.WriteLine("    " + ex);
        }
    }
    return failures == 0 ? 0 : 1;
}

// Symbol-extraction smoke over real files: for every symbol-bearing file (bounded budget), extract and
// tally by extension, so a language whose grammar silently stopped producing symbols (e.g. a bad DLL or
// a grammar node-name drift) shows up as a zero. Fails if NOTHING extracted.
static async Task<int> CmdSymbols(string[] args)
{
    if (args.Length < 2) return Usage();
    var paths = await ResolveTargets(args[1]);
    if (paths is null) return 1;

    int overall = 0;
    foreach (var path in paths)
    {
        var walker = new FileWalker(new IgnoreRules());
        using var extractor = new TreeSitterSymbolExtractor();
        var byExt = new Dictionary<string, (int Files, int Symbols)>(StringComparer.OrdinalIgnoreCase);
        long acc = 0; const long budget = 200L * 1024 * 1024;

        foreach (var f in walker.Walk(path))
        {
            if (acc >= budget) break;
            if (LanguageRegistry.ForPath(f.RelativePath) is null) continue; // only symbol-bearing languages
            string content;
            try
            {
                var bytes = File.ReadAllBytes(f.FullPath);
                if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;
                content = TextDecoder.FromBytes(bytes);
                acc += bytes.Length;
            }
            catch { continue; }

            int n = extractor.Extract(f.RelativePath, content).Count;
            var ext = Path.GetExtension(f.RelativePath).ToLowerInvariant();
            var cur = byExt.TryGetValue(ext, out var v) ? v : (Files: 0, Symbols: 0);
            byExt[ext] = (cur.Files + 1, cur.Symbols + n);
        }

        Console.WriteLine();
        Console.WriteLine($"[{Path.GetFileName(path)}] symbols by extension:");
        int total = 0;
        foreach (var kv in byExt.OrderByDescending(k => k.Value.Symbols))
        {
            Console.WriteLine($"  {kv.Key,-6} {kv.Value.Files,6:N0} files  {kv.Value.Symbols,9:N0} symbols");
            total += kv.Value.Symbols;
        }
        if (total == 0)
        {
            Console.WriteLine("  FAIL - no symbols extracted from any symbol-bearing file (grammar not loading?).");
            overall = 1;
        }
        else Console.WriteLine($"  PASS - {total:N0} symbols across {byExt.Count} language(s).");
    }
    return overall;
}

// Resolve a CLI target into one-or-more local repo paths: a manifest corpus id (fetch it), a whole tier
// (fetch+return every corpus in it), or a local directory path. Null on a bad path.
static async Task<List<string>?> ResolveTargets(string selector)
{
    var manifest = File.Exists(ManifestPath()) ? CorpusManifest.Load(ManifestPath()) : new CorpusManifest();
    var matches = manifest.Corpora.Where(c => c.Id == selector).ToList();
    if (matches.Count == 0) matches = manifest.Corpora.Where(c => c.Tier == selector).ToList();

    if (matches.Count > 0)
    {
        var paths = new List<string>();
        foreach (var e in matches) paths.Add(await CorpusFetcher.FetchAsync(e));
        return paths;
    }

    var path = Path.GetFullPath(selector);
    if (!Directory.Exists(path)) { Console.Error.WriteLine($"not a corpus id/tier or directory: {selector}"); return null; }
    return new List<string> { path };
}

static int CmdAll(string[] args)
{
    var selector = args.Length > 1 ? args[1] : "all";
    var manifest = File.Exists(ManifestPath()) ? CorpusManifest.Load(ManifestPath()) : new CorpusManifest();
    var targets = manifest.Corpora.Where(c => selector == "all" || c.Tier == selector || c.Id == selector).ToList();

    var rows = new List<ReportRow>();
    var skipped = new List<string>();

    foreach (var c in targets)
    {
        var root = CorpusFetcher.LocalRoot(c.Id);
        if (root is null) { skipped.Add(c.Id); continue; }

        try
        {
            Console.Error.WriteLine($"[{c.Id}] benchmarking...");
            var bench = Benchmark.Run(root);
            Console.Error.WriteLine($"[{c.Id}] verifying...");
            var oracle = Verifier.LexicalOracle(root, 100L * 1024 * 1024, 40);
            var correctness = oracle.Mismatches == 0 ? "PASS" : $"FAIL ({oracle.Mismatches})";
            rows.Add(new ReportRow(c.Id, c.Language, bench, correctness));

            Console.WriteLine();
            bench.Print(Console.Out);
            Console.WriteLine($"Correctness:       {correctness} ({oracle.Queries} oracle queries)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{c.Id}] ERROR: {ex.GetType().Name}: {ex.Message}");
            skipped.Add($"{c.Id} (error)");
        }
    }

    if (rows.Count == 0)
    {
        Console.Error.WriteLine("no fetched corpora to report. Fetch some first (fetch-corpus.ps1 or `bench fetch`).");
        return 1;
    }

    var meta = new ReportMeta(
        DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        Environment.MachineName,
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Environment.ProcessorCount,
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

    var html = HtmlReport.Generate(rows, meta, skipped);

    var benchDir = Path.GetDirectoryName(Path.GetFullPath(ManifestPath()))!;
    var resultsDir = Path.Combine(benchDir, "results");
    Directory.CreateDirectory(resultsDir);
    var outPath = Path.Combine(resultsDir, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
    File.WriteAllText(outPath, html);

    Console.WriteLine($"report: {outPath}");
    Console.WriteLine($"  {rows.Count} repo(s) benchmarked, {skipped.Count} skipped (not fetched)");
    return 0;
}

static int RunAndReport(string path)
{
    var result = Benchmark.Run(path);
    Console.WriteLine();
    result.Print(Console.Out);

    var benchDir = Path.GetDirectoryName(Path.GetFullPath(ManifestPath()))!;
    var resultsDir = Path.Combine(benchDir, "results");
    Directory.CreateDirectory(resultsDir);
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    File.WriteAllText(Path.Combine(resultsDir, $"{result.Name}-{stamp}.json"), result.ToJson());

    CompareToBaseline(benchDir, result);
    return 0;
}

static void CompareToBaseline(string benchDir, BenchResult current)
{
    var baselinePath = Path.Combine(benchDir, "baselines", $"{current.Name}.json");
    if (!File.Exists(baselinePath))
    {
        Console.WriteLine();
        Console.WriteLine($"(no baseline yet - to pin one: copy this result to {baselinePath})");
        return;
    }

    var baseline = JsonSerializer.Deserialize<BenchResult>(File.ReadAllText(baselinePath));
    if (baseline is null) return;

    Console.WriteLine();
    Console.WriteLine("vs baseline:");
    bool regressed = false;

    if (baseline.BuildMBps > 0 && current.BuildMBps < baseline.BuildMBps * 0.85)
    {
        Console.WriteLine($"  REGRESSION build: {current.BuildMBps:N1} MB/s vs {baseline.BuildMBps:N1} MB/s");
        regressed = true;
    }
    if (baseline.QueryP95Ms > 0 && current.QueryP95Ms > baseline.QueryP95Ms * 1.25)
    {
        Console.WriteLine($"  REGRESSION query p95: {current.QueryP95Ms:N2}ms vs {baseline.QueryP95Ms:N2}ms");
        regressed = true;
    }
    if (!regressed) Console.WriteLine("  OK (within thresholds)");
}
