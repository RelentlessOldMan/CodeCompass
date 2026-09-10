using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;

namespace CodeCompass.Bench;

public sealed record BenchResult(
    string Name,
    int Files,
    long Bytes,
    long Trigrams,
    int Symbols,
    double BuildSeconds,
    double BuildMBps,
    int Cores,
    double BuildMBpsPerCore,
    long IndexBytes,
    double IndexRatio,
    double QueryP50Ms,
    double QueryP95Ms,
    double QueryP99Ms,
    long PeakWorkingSetMb,
    long ManagedHeapMb,
    double IncrementalSeconds,
    int IncrementalFiles)
{
    public void Print(TextWriter w)
    {
        double mb = Bytes / (1024.0 * 1024.0);
        w.WriteLine($"Corpus:            {Name}");
        w.WriteLine($"Files indexed:     {Files:N0}  ({mb:N1} MB text)");
        w.WriteLine($"Trigrams / symbols:{Trigrams,12:N0} / {Symbols:N0}");
        w.WriteLine($"Build:             {BuildSeconds:N2}s   ({BuildMBps:N1} MB/s across {Cores} core(s), {BuildMBpsPerCore:N1} MB/s/core)");
        w.WriteLine($"Index on disk:     {IndexBytes / (1024.0 * 1024.0):N1} MB  ({IndexRatio:N2}x corpus)");
        w.WriteLine($"Query latency:     p50 {QueryP50Ms:N2}ms  p95 {QueryP95Ms:N2}ms  p99 {QueryP99Ms:N2}ms");
        w.WriteLine($"Incremental:       {IncrementalSeconds:N3}s for {IncrementalFiles} added file(s)");
        w.WriteLine($"Memory:            {ManagedHeapMb:N0} MB heap, {PeakWorkingSetMb:N0} MB peak working set");
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public static class Benchmark
{
    // Generic tokens that hit in almost any codebase; used to time the search hot path.
    private static readonly string[] Queries =
    {
        "return", "function", "class", "public", "private", "import",
        "error", "string", "value", "null", "void", "const", "for ", "while", "if (",
    };

    public static BenchResult Run(string path)
    {
        path = Path.GetFullPath(path);
        var name = new DirectoryInfo(path).Name;

        // Start from a compacted baseline so this repo's memory isn't inflated by a prior run.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        TrimWorkingSet(); // return retained pages so the sampled peak reflects only this repo

        // Sample the working set across this run to get a true peak (PeakWorkingSet64 is a
        // sticky process-lifetime value, useless when benchmarking several repos in one process).
        long peakWs = 0;
        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(() =>
        {
            var p = Process.GetCurrentProcess();
            while (!cts.IsCancellationRequested)
            {
                p.Refresh();
                var ws = p.WorkingSet64;
                if (ws > peakWs) peakWs = ws;
                Thread.Sleep(50);
            }
        });

        var (text, symbols, stats) = RepositoryIndexer.Build(path);
        var snapshot = LoadSnapshot(path);

        long indexBytes = IndexCacheBytes(path);
        double mb = stats.Bytes / (1024.0 * 1024.0);
        double mbps = stats.Seconds > 0 ? mb / stats.Seconds : 0;
        double ratio = stats.Bytes > 0 ? (double)indexBytes / stats.Bytes : 0;

        // Query latency: several runs per query, aggregate percentiles across all runs.
        var latencies = new List<double>();
        foreach (var q in Queries)
        {
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = text.Search(q, 50);
                sw.Stop();
                latencies.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        latencies.Sort();

        var (incSeconds, incFiles) = MeasureIncremental(text, symbols, snapshot, path);
        snapshot.Dispose();

        cts.Cancel();
        try { sampler.Wait(); } catch { /* sampler shutting down */ }

        long managedMb = GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);
        long peakWsMb = peakWs / (1024 * 1024);
        double perCore = stats.Cores > 0 ? mbps / stats.Cores : mbps;
        text.Dispose();
        symbols.Dispose();

        return new BenchResult(
            name, stats.Files, stats.Bytes, stats.TrigramPostings, stats.Symbols,
            stats.Seconds, mbps, stats.Cores, perCore, indexBytes, ratio,
            Percentile(latencies, 0.50), Percentile(latencies, 0.95), Percentile(latencies, 0.99),
            peakWsMb, managedMb, incSeconds, incFiles);
    }

    // Measures the live targeted-apply path (what the MCP server does when files change):
    // add temp files, time ApplyChanges against the in-memory index, then remove them.
    // Non-destructive - never touches existing files and doesn't persist the probe edits.
    private static (double seconds, int files) MeasureIncremental(
        SegmentedIndex text, SegmentedSymbolIndex symbols, DiskSnapshot snapshot, string root)
    {
        const int count = 10;
        var added = new List<string>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var p = Path.Combine(root, $"_ccbench_{i}.cs");
                File.WriteAllText(p, $"namespace CcBench {{ class Probe{i} {{ void M{i}() {{ }} }} }}\n");
                added.Add(p);
            }

            var sw = Stopwatch.StartNew();
            RepositoryIndexer.ApplyChanges(text, symbols, snapshot, root, added);
            sw.Stop();
            return (sw.Elapsed.TotalSeconds, count);
        }
        finally
        {
            foreach (var p in added)
                try { File.Delete(p); } catch { /* best effort */ }
            try { RepositoryIndexer.ApplyChanges(text, symbols, snapshot, root, added); } catch { }
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
        catch { /* non-Windows or not permitted: peak WS will just be less precise */ }
    }

    private static DiskSnapshot LoadSnapshot(string root) => RepositoryIndexer.LoadSnapshot(root);

    private static long IndexCacheBytes(string root)
    {
        try
        {
            var dir = IndexStore.GetCacheDir(root);
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                                         .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        idx = Math.Clamp(idx, 0, sorted.Count - 1);
        return sorted[idx];
    }
}
