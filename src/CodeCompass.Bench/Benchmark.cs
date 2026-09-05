using System.Diagnostics;
using System.Text.Json;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;

namespace CodeCompass.Bench;

public sealed record BenchResult(
    string Name,
    int Files,
    long Bytes,
    int Trigrams,
    int Symbols,
    double BuildSeconds,
    double BuildMBps,
    long IndexBytes,
    double IndexRatio,
    double QueryP50Ms,
    double QueryP95Ms,
    double QueryP99Ms,
    long PeakWorkingSetMb,
    double IncrementalSeconds,
    int IncrementalFiles)
{
    public void Print(TextWriter w)
    {
        double mb = Bytes / (1024.0 * 1024.0);
        w.WriteLine($"Corpus:            {Name}");
        w.WriteLine($"Files indexed:     {Files:N0}  ({mb:N1} MB text)");
        w.WriteLine($"Trigrams / symbols:{Trigrams,12:N0} / {Symbols:N0}");
        w.WriteLine($"Build:             {BuildSeconds:N2}s   ({BuildMBps:N1} MB/s)");
        w.WriteLine($"Index on disk:     {IndexBytes / (1024.0 * 1024.0):N1} MB  ({IndexRatio:N2}x corpus)");
        w.WriteLine($"Query latency:     p50 {QueryP50Ms:N2}ms  p95 {QueryP95Ms:N2}ms  p99 {QueryP99Ms:N2}ms");
        w.WriteLine($"Incremental:       {IncrementalSeconds:N3}s for {IncrementalFiles} added file(s)");
        w.WriteLine($"Peak working set:  {PeakWorkingSetMb:N0} MB");
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

        GC.Collect();
        GC.WaitForPendingFinalizers();

        var (text, _, stats) = RepositoryIndexer.Build(path);

        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long peakMb = proc.PeakWorkingSet64 / (1024 * 1024);

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

        var (incSeconds, incFiles) = MeasureIncremental(path);

        return new BenchResult(
            name, stats.Files, stats.Bytes, stats.Trigrams, stats.Symbols,
            stats.Seconds, mbps, indexBytes, ratio,
            Percentile(latencies, 0.50), Percentile(latencies, 0.95), Percentile(latencies, 0.99),
            peakMb, incSeconds, incFiles);
    }

    // Non-destructive: add temp files, time the incremental update, then remove them.
    // Never modifies existing files, so it's safe to point at a real repo.
    private static (double seconds, int files) MeasureIncremental(string root)
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
            RepositoryIndexer.Update(root);
            sw.Stop();
            return (sw.Elapsed.TotalSeconds, count);
        }
        finally
        {
            foreach (var p in added)
                try { File.Delete(p); } catch { /* best effort */ }
            try { RepositoryIndexer.Update(root); } catch { /* restore index to clean state */ }
        }
    }

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
