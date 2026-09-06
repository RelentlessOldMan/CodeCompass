using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Text;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Indexing;

public sealed record IndexStats(int Files, long Bytes, int Trigrams, int Symbols, double Seconds, long IndexBytes, int Cores);

public sealed record UpdateStats(int Added, int Modified, int Removed, double Seconds, bool FullRebuild);

public sealed record ChangeCounts(int Added, int Modified, int Removed);

/// <summary>
/// Builds and loads a repository's on-disk index (segmented, memory-mapped trigram index +
/// symbols + a content snapshot for change detection). Shared by the CLI and MCP server.
/// A full build reads each file once and streams trigram postings to disk segments so build
/// memory stays bounded regardless of repo size; <see cref="Update"/>/<see cref="UpdatePaths"/>
/// reconcile incrementally. Callers own returned <see cref="SegmentedIndex"/> instances and
/// should Dispose long-lived ones.
/// </summary>
public static class RepositoryIndexer
{
    // Above this many changed files an incremental update isn't worth it; rebuild instead.
    private const int RebuildThreshold = 2000;

    public static (SegmentedIndex Text, SymbolIndex Symbols, IndexStats Stats) Build(string root)
    {
        root = Path.GetFullPath(root);
        var dir = IndexStore.GetCacheDir(root);
        var walker = new FileWalker(new IgnoreRules());
        int cores = DegreeOfParallelism();
        long budget = SegmentedIndex.DefaultBudgetBytes;

        var symbols = new SymbolIndex();
        var snapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var segFiles = new ConcurrentBag<(int Num, string Name)>();
        long totalBytes = 0;
        var gate = new object();
        int segCounter = SegmentedIndex.NextSegmentNumber(dir); // monotonic; never reuse/overwrite

        var sw = Stopwatch.StartNew();

        // Each worker fills its own SegmentBuilder lock-free and flushes a segment file when it
        // crosses the byte budget - so build RAM is ~cores x budget, not O(repo). Symbols and the
        // snapshot are merged under one lock per event (cheap relative to parse/trigram work).
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

                worker.Builder.AddDocument(file.RelativePath, TrigramIndex.ComputeTrigrams(content));
                if (LanguageRegistry.ForPath(file.RelativePath) is not null)
                    worker.Symbols.AddRange(worker.Extractor.Extract(file.RelativePath, content));
                worker.Snapshot[file.RelativePath] = new FileState(bytes.Length, mtime, hash);
                worker.Bytes += bytes.Length;

                if (worker.Builder.ApproxBytes >= budget)
                    FlushWorkerSegment(worker, dir, ref segCounter, segFiles);
                return worker;
            },
            worker =>
            {
                FlushWorkerSegment(worker, dir, ref segCounter, segFiles);
                lock (gate)
                {
                    foreach (var s in worker.Symbols) symbols.Add(s);
                    foreach (var (rel, state) in worker.Snapshot) snapshot[rel] = state;
                    totalBytes += worker.Bytes;
                }
                worker.Extractor.Dispose();
            });

        sw.Stop();

        var ordered = segFiles.OrderBy(x => x.Num).Select(x => x.Name).ToList();
        var text = SegmentedIndex.FromSegmentFiles(root, dir, ordered, segCounter, budget);
        SaveSymbols(root, symbols);
        SaveSnapshot(root, snapshot);

        var stats = new IndexStats(text.DocumentCount, totalBytes, (int)text.TotalTerms,
                                   symbols.Count, sw.Elapsed.TotalSeconds, text.IndexBytes, cores);
        return (text, symbols, stats);
    }

    private static void FlushWorkerSegment(BuildWorker worker, string dir, ref int segCounter,
                                           ConcurrentBag<(int, string)> segFiles)
    {
        if (worker.Builder.DocCount == 0) return;
        int num = Interlocked.Increment(ref segCounter) - 1;
        var name = SegmentedIndex.SegmentFileName(num);
        worker.Builder.WriteTo(Path.Combine(dir, name));
        segFiles.Add((num, name));
        worker.Builder = new SegmentBuilder();
    }

    /// <summary>Indexing parallelism: CODECOMPASS_THREADS if set (and valid), else all cores.</summary>
    public static int DegreeOfParallelism()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_THREADS");
        if (int.TryParse(env, out var n) && n > 0) return n;
        return Environment.ProcessorCount;
    }

    private sealed class BuildWorker
    {
        public SegmentBuilder Builder = new();
        public List<Symbol> Symbols { get; } = new();
        public Dictionary<string, FileState> Snapshot { get; } = new(StringComparer.Ordinal);
        public TreeSitterSymbolExtractor Extractor { get; } = new();
        public long Bytes;
    }

    /// <summary>
    /// Incrementally reconcile the on-disk index with the current tree. Only files whose
    /// size+mtime changed are read; touched-but-identical files are skipped. Falls back to a
    /// full rebuild if there's no prior index or too many files changed (e.g. a branch switch).
    /// </summary>
    public static (SegmentedIndex Text, SymbolIndex Symbols, UpdateStats Stats) Update(string root)
    {
        root = Path.GetFullPath(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols) || !TryLoadSnapshot(root, out var old))
            return FullRebuild(root, text, sw);

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
                    newSnapshot[rel] = os; // unchanged: no read
                    continue;
                }

                byte[] bytes;
                try { bytes = File.ReadAllBytes(file.FullPath); }
                catch { continue; }
                if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;

                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (old.TryGetValue(rel, out var os2) && os2.ContentHash == hash)
                {
                    newSnapshot[rel] = new FileState(bytes.Length, mtime, hash); // touched but identical
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
            return FullRebuild(root, text, sw);

        SaveAll(root, text, symbols, newSnapshot);
        sw.Stop();
        return (text, symbols, new UpdateStats(added, modified, removed, sw.Elapsed.TotalSeconds, false));
    }

    /// <summary>Disk-based targeted update: apply just the given changed paths. Never re-walks
    /// the whole tree, so it's cheap for small changes.</summary>
    public static (SegmentedIndex Text, SymbolIndex Symbols, UpdateStats Stats) UpdatePaths(
        string root, IReadOnlyCollection<string> changedFullPaths)
    {
        root = Path.GetFullPath(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols) || !TryLoadSnapshot(root, out var snapshot))
            return FullRebuild(root, text, sw);
        if (changedFullPaths.Count > RebuildThreshold)
            return FullRebuild(root, text, sw);

        var counts = ApplyChanges(text, symbols, snapshot, root, changedFullPaths);
        SaveAll(root, text, symbols, snapshot);
        return (text, symbols, new UpdateStats(counts.Added, counts.Modified, counts.Removed, sw.Elapsed.TotalSeconds, false));
    }

    private static (SegmentedIndex, SymbolIndex, UpdateStats) FullRebuild(string root, SegmentedIndex? old, Stopwatch sw)
    {
        old?.Dispose(); // release mmaps before rebuilding
        var b = Build(root);
        sw.Stop();
        return (b.Text, b.Symbols, new UpdateStats(b.Stats.Files, 0, 0, b.Stats.Seconds, FullRebuild: true));
    }

    /// <summary>
    /// Apply changed paths to already-loaded in-memory indexes (does not persist). Handles files
    /// (add/modify/delete/touched-identical), new/renamed directories (re-index the subtree), and
    /// deleted directories (remove everything under the prefix).
    /// </summary>
    public static ChangeCounts ApplyChanges(
        SegmentedIndex text, SymbolIndex symbols, Dictionary<string, FileState> snapshot,
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

    /// <summary>Persist in-memory index + symbols + snapshot to the cache.</summary>
    public static void Persist(string root, SegmentedIndex text, SymbolIndex symbols,
                               IReadOnlyDictionary<string, FileState> snapshot) =>
        SaveAll(root, text, symbols, snapshot);

    private static void ApplyFile(
        SegmentedIndex text, SymbolIndex symbols, Dictionary<string, FileState> snapshot,
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
            snapshot[rel] = new FileState(bytes.Length, mtime, hash); // touched but identical
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
        SegmentedIndex text, SymbolIndex symbols, Dictionary<string, FileState> snapshot,
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

    public static bool TryLoad(string root, out SegmentedIndex text, out SymbolIndex symbols)
    {
        root = Path.GetFullPath(root);
        text = null!;
        symbols = null!;
        var dir = IndexStore.GetCacheDir(root);
        try
        {
            var symbolPath = IndexStore.SymbolIndexPath(root);
            if (!SegmentedIndex.Exists(dir) || !File.Exists(symbolPath)) return false;
            text = SegmentedIndex.Open(root, dir);
            using (var fs = File.OpenRead(symbolPath)) symbols = SymbolIndex.Load(fs);
            return true;
        }
        catch
        {
            text?.Dispose();
            text = null!;
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

    private static void SaveAll(string root, SegmentedIndex text, SymbolIndex symbols,
                               IReadOnlyDictionary<string, FileState> snapshot)
    {
        text.Flush();
        SaveSymbols(root, symbols);
        SaveSnapshot(root, snapshot);
    }

    private static void SaveSymbols(string root, SymbolIndex symbols)
    {
        using var fs = File.Create(IndexStore.SymbolIndexPath(root));
        symbols.Save(fs);
    }

    private static void SaveSnapshot(string root, IReadOnlyDictionary<string, FileState> snapshot)
    {
        using var fs = File.Create(IndexStore.SnapshotPath(root));
        SnapshotStore.Save(fs, snapshot);
    }
}
