using System.Text.Json;
using CodeCompass.Bench;

if (args.Length == 0) return Usage();

switch (args[0].ToLowerInvariant())
{
    case "list": return CmdList();
    case "fetch": return await CmdFetch(args);
    case "run": return CmdRun(args);
    case "bench": return await CmdBench(args);
    case "verify": return await CmdVerify(args);
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
    Console.Error.WriteLine("  bench verify <path|id> [budgetMB] [queries]");
    Console.Error.WriteLine("                                 correctness: trigram search vs brute-force on real files");
    Console.Error.WriteLine();
    Console.Error.WriteLine("env: CODECOMPASS_MANIFEST (manifest path), CODECOMPASS_CORPUS_DIR (cache dir)");
    return 1;
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

    // Argument is either a manifest corpus id (fetch it) or a local path.
    string path;
    var manifest = File.Exists(ManifestPath()) ? CorpusManifest.Load(ManifestPath()) : new CorpusManifest();
    var entry = manifest.Corpora.FirstOrDefault(c => c.Id == args[1]);
    if (entry is not null)
    {
        path = await CorpusFetcher.FetchAsync(entry);
    }
    else
    {
        path = Path.GetFullPath(args[1]);
        if (!Directory.Exists(path)) { Console.Error.WriteLine($"not a directory: {path}"); return 1; }
    }

    long budgetMb = args.Length > 2 && long.TryParse(args[2], out var b) ? b : 100;
    int queries = args.Length > 3 && int.TryParse(args[3], out var q) ? q : 40;

    var r = Verifier.LexicalOracle(path, budgetMb * 1024 * 1024, queries);
    Console.WriteLine();
    Console.WriteLine($"Lexical oracle: {r.Queries} queries over {r.SubsetFiles:N0} files " +
                      $"({r.SubsetBytes / (1024.0 * 1024.0):N1} MB verified)");
    if (r.Mismatches == 0)
    {
        Console.WriteLine("PASS - trigram search matches the brute-force scan exactly.");
        return 0;
    }
    Console.WriteLine($"FAIL - {r.Mismatches} mismatch(es):");
    foreach (var ex in r.Examples) Console.WriteLine("  " + ex);
    return 1;
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
