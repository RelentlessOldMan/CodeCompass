using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using CodeCompass.Core.Text;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Indexing;

public sealed record IndexStats(int Files, long Bytes, int Trigrams, int Symbols, double Seconds, long IndexBytes, int Cores);

public sealed record UpdateStats(int Added, int Modified, int Removed, double Seconds, bool FullRebuild);

public sealed record ChangeCounts(int Added, int Modified, int Removed);

/// <summary>
/// Builds and loads a repository's on-disk index: a memory-mapped segmented trigram index,
/// a memory-mapped segmented symbol index, and a content snapshot for change detection.
/// Both indexes stream to disk during build (bounded RAM) and are read via mmap (bounded
/// RAM at query time). Callers own returned index instances and should Dispose long-lived ones.
/// </summary>
public static class RepositoryIndexer
{
    private const int RebuildThreshold = 2000;

    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, IndexStats Stats) Build(string root)
    {
        root = Path.GetFullPath(root);
        var dir = IndexStore.GetCacheDir(root);
        var walker = new FileWalker(new IgnoreRules());
        int cores = DegreeOfParallelism();
        var (textBudget, symBudget) = SegmentBudgets(cores);

        var snapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var textSegFiles = new ConcurrentBag<(int Num, string Name)>();
        var symSegFiles = new ConcurrentBag<(int Num, string Name)>();
        int textSegCounter = SegmentedIndex.NextSegmentNumber(dir);
        int symSegCounter = SegmentedSymbolIndex.NextSegmentNumber(dir);
        long totalBytes = 0;
        var gate = new object();

        var sw = Stopwatch.StartNew();

        // Each worker fills private trigram + symbol segment buffers lock-free and flushes them
        // to disk at their byte budgets, so build RAM is bounded regardless of repo size.
        Parallel.ForEach(
            walker.Walk(root),
            new ParallelOptions { MaxDegreeOfParallelism = cores },
            () => new BuildWorker(),
            (file, _, worker) =>
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file.FullPath); }
                catch { return worker; }
                if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) return worker;

                var content = TextDecoder.FromBytes(bytes);
                var mtime = File.GetLastWriteTimeUtc(file.FullPath).Ticks;
                var hash = Convert.ToHexString(SHA256.HashData(bytes));

                worker.Text.AddDocument(file.RelativePath, TrigramIndex.ComputeTrigrams(content));
                if (LanguageRegistry.ForPath(file.RelativePath) is not null)
                    foreach (var s in worker.Extractor.Extract(file.RelativePath, content))
                        worker.Symbols.Add(s);
                worker.Snapshot[file.RelativePath] = new FileState(bytes.Length, mtime, hash);
                worker.Bytes += bytes.Length;

                if (worker.Text.ApproxBytes >= textBudget) FlushText(worker, dir, ref textSegCounter, textSegFiles);
                if (worker.Symbols.ApproxBytes >= symBudget) FlushSymbols(worker, dir, ref symSegCounter, symSegFiles);
                return worker;
            },
            worker =>
            {
                FlushText(worker, dir, ref textSegCounter, textSegFiles);
                FlushSymbols(worker, dir, ref symSegCounter, symSegFiles);
                lock (gate)
                {
                    foreach (var (rel, state) in worker.Snapshot) snapshot[rel] = state;
                    totalBytes += worker.Bytes;
                }
                worker.Extractor.Dispose();
            });

        sw.Stop();

        var textOrdered = textSegFiles.OrderBy(x => x.Num).Select(x => x.Name).ToList();
        var symOrdered = symSegFiles.OrderBy(x => x.Num).Select(x => x.Name).ToList();
        var text = SegmentedIndex.FromSegmentFiles(root, dir, textOrdered, textSegCounter, textBudget);
        var symbols = SegmentedSymbolIndex.FromSegmentFiles(dir, symOrdered, symSegCounter, symBudget);
        SaveSnapshot(root, snapshot);

        var stats = new IndexStats(text.DocumentCount, totalBytes, (int)text.TotalTerms,
                                   symbols.Count, sw.Elapsed.TotalSeconds, text.IndexBytes, cores);
        return (text, symbols, stats);
    }

    private static void FlushText(BuildWorker w, string dir, ref int counter, ConcurrentBag<(int, string)> files)
    {
        if (w.Text.DocCount == 0) return;
        int num = Interlocked.Increment(ref counter) - 1;
        var name = SegmentedIndex.SegmentFileName(num);
        w.Text.WriteTo(Path.Combine(dir, name));
        files.Add((num, name));
        w.Text = new SegmentBuilder();
    }

    private static void FlushSymbols(BuildWorker w, string dir, ref int counter, ConcurrentBag<(int, string)> files)
    {
        if (w.Symbols.Count == 0) return;
        int num = Interlocked.Increment(ref counter) - 1;
        var name = SegmentedSymbolIndex.SegmentFileName(num);
        w.Symbols.WriteTo(Path.Combine(dir, name));
        files.Add((num, name));
        w.Symbols = new SymbolSegmentBuilder();
    }

    /// <summary>Indexing parallelism: CODECOMPASS_THREADS if set (and valid), else all cores.</summary>
    public static int DegreeOfParallelism()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_THREADS");
        if (int.TryParse(env, out var n) && n > 0) return n;
        return Environment.ProcessorCount;
    }

    /// <summary>
    /// Per-worker trigram/symbol segment byte budgets, scaled so total build buffers
    /// (cores x (text+symbol)) fit a fraction of available RAM - keeps first-time builds
    /// within reach on small machines. Override the text budget with CODECOMPASS_SEGMENT_MB.
    /// </summary>
    public static (long Text, long Symbol) SegmentBudgets(int cores)
    {
        cores = Math.Max(1, cores);
        var envMb = Environment.GetEnvironmentVariable("CODECOMPASS_SEGMENT_MB");
        if (int.TryParse(envMb, out var mb) && mb > 0)
        {
            long t = mb * 1024L * 1024;
            return (t, Math.Max(2L * 1024 * 1024, t / 2));
        }

        long avail;
        try { avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { avail = 8L * 1024 * 1024 * 1024; }
        if (avail <= 0) avail = 8L * 1024 * 1024 * 1024;

        long totalBuffers = Math.Clamp(avail / 8, 128L * 1024 * 1024, 4L * 1024 * 1024 * 1024);
        long perCore = totalBuffers / cores;
        long text = Math.Clamp(perCore * 2 / 3, 4L * 1024 * 1024, 128L * 1024 * 1024);
        long symbol = Math.Clamp(perCore / 3, 2L * 1024 * 1024, 64L * 1024 * 1024);
        return (text, symbol);
    }

    private sealed class BuildWorker
    {
        public SegmentBuilder Text = new();
        public SymbolSegmentBuilder Symbols = new();
        public Dictionary<string, FileState> Snapshot { get; } = new(StringComparer.Ordinal);
        public TreeSitterSymbolExtractor Extractor { get; } = new();
        public long Bytes;
    }

    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, UpdateStats Stats) Update(string root)
    {
        root = Path.GetFullPath(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols) || !TryLoadSnapshot(root, out var old))
            return FullRebuild(root, text, symbols, sw);

        var walker = new FileWalker(new IgnoreRules());
        var newSnapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int added = 0, modified = 0, removed = 0;

        using (var extractor = new TreeSitterSymbolExtractor())
        {
            foreach (var file in walker.Walk(root))
            {
                var rel = file.RelativePath;
                seen.Add(rel);
                var mtime = File.GetLastWriteTimeUtc(file.FullPath).Ticks;

                if (old.TryGetValue(rel, out var os) && os.Size == file.Size && os.MTimeTicks == mtime)
                {
                    newSnapshot[rel] = os;
                    continue;
                }

                byte[] bytes;
                try { bytes = File.ReadAllBytes(file.FullPath); }
                catch { continue; }
                if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;

                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (old.TryGetValue(rel, out var os2) && os2.ContentHash == hash)
                {
                    newSnapshot[rel] = new FileState(bytes.Length, mtime, hash);
                    continue;
                }

                var content = TextDecoder.FromBytes(bytes);
                text.RemovePath(rel);
                text.AddDocumentText(rel, content);
                symbols.RemovePath(rel);
                if (LanguageRegistry.ForPath(rel) is not null)
                    symbols.AddForPath(rel, extractor.Extract(rel, content));
                newSnapshot[rel] = new FileState(bytes.Length, mtime, hash);
                if (old.ContainsKey(rel)) modified++; else added++;
            }
        }

        foreach (var rel in old.Keys)
        {
            if (seen.Contains(rel)) continue;
            text.RemovePath(rel);
            symbols.RemovePath(rel);
            removed++;
        }

        if (added + modified + removed > RebuildThreshold)
            return FullRebuild(root, text, symbols, sw);

        SaveAll(root, text, symbols, newSnapshot);
        sw.Stop();
        return (text, symbols, new UpdateStats(added, modified, removed, sw.Elapsed.TotalSeconds, false));
    }

    /// <summary>Disk-based targeted update: apply just the given changed paths.</summary>
    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, UpdateStats Stats) UpdatePaths(
        string root, IReadOnlyCollection<string> changedFullPaths)
    {
        root = Path.GetFullPath(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols) || !TryLoadSnapshot(root, out var snapshot))
            return FullRebuild(root, text, symbols, sw);
        if (changedFullPaths.Count > RebuildThreshold)
            return FullRebuild(root, text, symbols, sw);

        var counts = ApplyChanges(text, symbols, snapshot, root, changedFullPaths);
        SaveAll(root, text, symbols, snapshot);
        return (text, symbols, new UpdateStats(counts.Added, counts.Modified, counts.Removed, sw.Elapsed.TotalSeconds, false));
    }

    private static (SegmentedIndex, SegmentedSymbolIndex, UpdateStats) FullRebuild(
        string root, SegmentedIndex? oldText, SegmentedSymbolIndex? oldSymbols, Stopwatch sw)
    {
        oldText?.Dispose();
        oldSymbols?.Dispose();
        var b = Build(root);
        sw.Stop();
        return (b.Text, b.Symbols, new UpdateStats(b.Stats.Files, 0, 0, b.Stats.Seconds, FullRebuild: true));
    }

    /// <summary>Apply changed paths to already-loaded in-memory indexes (does not persist).</summary>
    public static ChangeCounts ApplyChanges(
        SegmentedIndex text, SegmentedSymbolIndex symbols, Dictionary<string, FileState> snapshot,
        string root, IEnumerable<string> changedFullPaths)
    {
        root = Path.GetFullPath(root);
        var ignore = new IgnoreRules();
        using var extractor = new TreeSitterSymbolExtractor();
        int added = 0, modified = 0, removed = 0;

        foreach (var full in changedFullPaths.Select(Path.GetFullPath).Distinct())
        {
            string rel;
            try { rel = Path.GetRelativePath(root, full).Replace('\\', '/'); }
            catch { continue; }
            if (rel.StartsWith("..", StringComparison.Ordinal) || rel == ".") continue;

            if (Directory.Exists(full))
            {
                foreach (var f in new FileWalker(ignore).Walk(full))
                {
                    var childRel = Path.GetRelativePath(root, f.FullPath).Replace('\\', '/');
                    ApplyFile(text, symbols, snapshot, childRel, f.FullPath, ignore, extractor,
                              ref added, ref modified, ref removed);
                }
            }
            else if (File.Exists(full))
            {
                ApplyFile(text, symbols, snapshot, rel, full, ignore, extractor,
                          ref added, ref modified, ref removed);
            }
            else
            {
                RemovePathAndChildren(text, symbols, snapshot, rel, ref removed);
            }
        }

        return new ChangeCounts(added, modified, removed);
    }

    /// <summary>Persist in-memory indexes + snapshot to the cache.</summary>
    public static void Persist(string root, SegmentedIndex text, SegmentedSymbolIndex symbols,
                               IReadOnlyDictionary<string, FileState> snapshot) =>
        SaveAll(root, text, symbols, snapshot);

    private static void ApplyFile(
        SegmentedIndex text, SegmentedSymbolIndex symbols, Dictionary<string, FileState> snapshot,
        string rel, string full, IgnoreRules ignore, TreeSitterSymbolExtractor extractor,
        ref int added, ref int modified, ref int removed)
    {
        bool wasPresent = snapshot.ContainsKey(rel);

        if (IsIgnoredRelPath(rel, ignore))
        {
            if (wasPresent) { text.RemovePath(rel); symbols.RemovePath(rel); snapshot.Remove(rel); removed++; }
            return;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch { return; }

        if (bytes.Length > ignore.MaxFileSizeBytes ||
            IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000))))
        {
            if (wasPresent) { text.RemovePath(rel); symbols.RemovePath(rel); snapshot.Remove(rel); removed++; }
            return;
        }

        var mtime = File.GetLastWriteTimeUtc(full).Ticks;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        if (snapshot.TryGetValue(rel, out var old) && old.ContentHash == hash)
        {
            snapshot[rel] = new FileState(bytes.Length, mtime, hash);
            return;
        }

        var content = TextDecoder.FromBytes(bytes);
        text.RemovePath(rel);
        text.AddDocumentText(rel, content);
        symbols.RemovePath(rel);
        if (LanguageRegistry.ForPath(rel) is not null)
            symbols.AddForPath(rel, extractor.Extract(rel, content));
        snapshot[rel] = new FileState(bytes.Length, mtime, hash);

        if (wasPresent) modified++; else added++;
    }

    private static void RemovePathAndChildren(
        SegmentedIndex text, SegmentedSymbolIndex symbols, Dictionary<string, FileState> snapshot,
        string rel, ref int removed)
    {
        if (snapshot.Remove(rel))
        {
            text.RemovePath(rel);
            symbols.RemovePath(rel);
            removed++;
        }

        var prefix = rel + "/";
        var children = snapshot.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var k in children)
        {
            snapshot.Remove(k);
            text.RemovePath(k);
            symbols.RemovePath(k);
            removed++;
        }
    }

    private static bool IsIgnoredRelPath(string rel, IgnoreRules ignore)
    {
        var parts = rel.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (ignore.IsIgnoredDirectory(parts[i]))
                return true;
        return ignore.IsIgnoredFile(parts[^1], 0);
    }

    public static bool TryLoad(string root, out SegmentedIndex text, out SegmentedSymbolIndex symbols)
    {
        root = Path.GetFullPath(root);
        text = null!;
        symbols = null!;
        var dir = IndexStore.GetCacheDir(root);
        try
        {
            if (!SegmentedIndex.Exists(dir) || !SegmentedSymbolIndex.Exists(dir)) return false;
            text = SegmentedIndex.Open(root, dir);
            symbols = SegmentedSymbolIndex.Open(dir);
            return true;
        }
        catch
        {
            text?.Dispose();
            symbols?.Dispose();
            text = null!;
            symbols = null!;
            return false;
        }
    }

    /// <summary>Load the change-detection snapshot for a repo (empty if none exists).</summary>
    public static Dictionary<string, FileState> LoadSnapshot(string root) =>
        TryLoadSnapshot(root, out var s) ? s : new Dictionary<string, FileState>(StringComparer.Ordinal);

    private static bool TryLoadSnapshot(string root, out Dictionary<string, FileState> snapshot)
    {
        snapshot = null!;
        try
        {
            var path = IndexStore.SnapshotPath(root);
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            snapshot = SnapshotStore.Load(fs);
            return true;
        }
        catch { return false; }
    }

    private static void SaveAll(string root, SegmentedIndex text, SegmentedSymbolIndex symbols,
                               IReadOnlyDictionary<string, FileState> snapshot)
    {
        text.Flush();
        symbols.Flush();
        SaveSnapshot(root, snapshot);
    }

    private static void SaveSnapshot(string root, IReadOnlyDictionary<string, FileState> snapshot)
    {
        using var fs = File.Create(IndexStore.SnapshotPath(root));
        SnapshotStore.Save(fs, snapshot);
    }
}
