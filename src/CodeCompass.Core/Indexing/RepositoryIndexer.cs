using System.Diagnostics;
using System.Security.Cryptography;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Text;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Indexing;

public sealed record IndexStats(int Files, long Bytes, int Trigrams, int Symbols, double Seconds, long IndexBytes, int Cores);

public sealed record UpdateStats(int Added, int Modified, int Removed, double Seconds, bool FullRebuild);

public sealed record ChangeCounts(int Added, int Modified, int Removed);

/// <summary>
/// Builds and loads a repository's on-disk indexes (trigram text + symbols) and the
/// content snapshot used for change detection. Shared by the CLI and the MCP server.
/// A full build reads each file exactly once (hashing, trigrams and symbols together);
/// <see cref="Update"/> reconciles incrementally, re-reading only files that changed.
/// </summary>
public static class RepositoryIndexer
{
    // Above this many changed files an incremental update isn't worth it; rebuild instead.
    private const int RebuildThreshold = 2000;

    public static (TrigramIndex Text, SymbolIndex Symbols, IndexStats Stats) Build(string root)
    {
        root = Path.GetFullPath(root);
        var walker = new FileWalker(new IgnoreRules());
        int cores = DegreeOfParallelism();

        var text = TrigramIndex.Create(root);
        var symbols = new SymbolIndex();
        var snapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
        long totalBytes = 0;
        var gate = new object();

        var sw = Stopwatch.StartNew();

        // Each worker accumulates a private, lock-free partial (its own trigram index,
        // symbol list, snapshot). The heavy per-file work (read, hash, trigram extraction,
        // tree-sitter parse) never contends; the only lock is one merge per worker at the
        // end - so contention is O(threads), not O(files).
        Parallel.ForEach(
            walker.Walk(root),
            new ParallelOptions { MaxDegreeOfParallelism = cores },
            () => new BuildWorker(root),
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
                    worker.Symbols.AddRange(worker.Extractor.Extract(file.RelativePath, content));
                worker.Snapshot[file.RelativePath] = new FileState(bytes.Length, mtime, hash);
                worker.Bytes += bytes.Length;
                return worker;
            },
            worker =>
            {
                lock (gate)
                {
                    text.MergeFrom(worker.Text);
                    foreach (var s in worker.Symbols) symbols.Add(s);
                    foreach (var (rel, state) in worker.Snapshot) snapshot[rel] = state;
                    totalBytes += worker.Bytes;
                }
                worker.Extractor.Dispose();
            });

        sw.Stop();

        SaveAll(root, text, symbols, snapshot);

        long indexBytes = new FileInfo(IndexStore.IndexPath(root)).Length;
        var stats = new IndexStats(text.DocumentCount, totalBytes, text.TrigramCount,
                                   symbols.Count, sw.Elapsed.TotalSeconds, indexBytes, cores);
        return (text, symbols, stats);
    }

    /// <summary>Indexing parallelism: CODECOMPASS_THREADS if set (and valid), else all cores.</summary>
    public static int DegreeOfParallelism()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_THREADS");
        if (int.TryParse(env, out var n) && n > 0) return n;
        return Environment.ProcessorCount;
    }

    // Per-thread accumulator: a private partial index merged into the global once at the end.
    private sealed class BuildWorker
    {
        public TrigramIndex Text { get; }
        public List<Symbol> Symbols { get; } = new();
        public Dictionary<string, FileState> Snapshot { get; } = new(StringComparer.Ordinal);
        public TreeSitterSymbolExtractor Extractor { get; } = new();
        public long Bytes;

        public BuildWorker(string root) => Text = TrigramIndex.Create(root);
    }

    /// <summary>
    /// Incrementally reconcile the on-disk indexes with the current file tree. Only files
    /// whose size+mtime changed are read; those whose content hash still matches are skipped
    /// (touched-but-identical). Falls back to a full rebuild if the index/snapshot is missing
    /// or if too many files changed at once (e.g. a branch switch).
    /// </summary>
    public static (TrigramIndex Text, SymbolIndex Symbols, UpdateStats Stats) Update(string root)
    {
        root = Path.GetFullPath(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols) || !TryLoadSnapshot(root, out var old))
        {
            var b = Build(root);
            return (b.Text, b.Symbols, new UpdateStats(b.Stats.Files, 0, 0, b.Stats.Seconds, FullRebuild: true));
        }

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

                // Fast path: unchanged size+mtime => reuse old state, no read.
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

                // Touched but identical content: refresh mtime/size, don't re-index.
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

        // Large change (branch switch etc.): a clean rebuild is cheaper and reclaims tombstones.
        if (added + modified + removed > RebuildThreshold)
        {
            var b = Build(root);
            return (b.Text, b.Symbols, new UpdateStats(added, modified, removed, sw.Elapsed.TotalSeconds, true));
        }

        SaveAll(root, text, symbols, newSnapshot);
        sw.Stop();
        return (text, symbols, new UpdateStats(added, modified, removed, sw.Elapsed.TotalSeconds, false));
    }

    /// <summary>
    /// Disk-based targeted update: apply just the given changed paths to the on-disk index.
    /// Falls back to a full build if there is no prior index, or to a full reconcile-scale
    /// rebuild if too many paths changed at once. Much cheaper than <see cref="Update"/> for
    /// small changes because it never re-walks the whole tree.
    /// </summary>
    public static (TrigramIndex Text, SymbolIndex Symbols, UpdateStats Stats) UpdatePaths(
        string root, IReadOnlyCollection<string> changedFullPaths)
    {
        root = Path.GetFullPath(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols) || !TryLoadSnapshot(root, out var snapshot))
        {
            var b = Build(root);
            return (b.Text, b.Symbols, new UpdateStats(b.Stats.Files, 0, 0, b.Stats.Seconds, true));
        }
        if (changedFullPaths.Count > RebuildThreshold)
        {
            var b = Build(root);
            return (b.Text, b.Symbols, new UpdateStats(b.Stats.Files, 0, 0, b.Stats.Seconds, true));
        }

        var counts = ApplyChanges(text, symbols, snapshot, root, changedFullPaths);
        SaveAll(root, text, symbols, snapshot);
        return (text, symbols, new UpdateStats(counts.Added, counts.Modified, counts.Removed, sw.Elapsed.TotalSeconds, false));
    }

    /// <summary>
    /// Apply a set of changed paths to already-loaded in-memory indexes (does not persist).
    /// Handles files (add/modify/delete/touched-identical), new or renamed directories
    /// (re-index the subtree), and deleted directories (remove everything under the prefix).
    /// </summary>
    public static ChangeCounts ApplyChanges(
        TrigramIndex text, SymbolIndex symbols, Dictionary<string, FileState> snapshot,
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
                // New or renamed directory: (re)index every file beneath it.
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
                // Vanished path: remove the exact entry and anything beneath it (deleted dir).
                RemovePathAndChildren(text, symbols, snapshot, rel, ref removed);
            }
        }

        return new ChangeCounts(added, modified, removed);
    }

    /// <summary>Persist in-memory indexes + snapshot to the cache.</summary>
    public static void Persist(string root, TrigramIndex text, SymbolIndex symbols,
                               IReadOnlyDictionary<string, FileState> snapshot) =>
        SaveAll(root, text, symbols, snapshot);

    private static void ApplyFile(
        TrigramIndex text, SymbolIndex symbols, Dictionary<string, FileState> snapshot,
        string rel, string full, IgnoreRules ignore, TreeSitterSymbolExtractor extractor,
        ref int added, ref int modified, ref int removed)
    {
        bool wasPresent = snapshot.ContainsKey(rel);

        // Ignored path (e.g. now under bin/, or an ignored extension): ensure it isn't indexed.
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
        TrigramIndex text, SymbolIndex symbols, Dictionary<string, FileState> snapshot,
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

    public static bool TryLoad(string root, out TrigramIndex text, out SymbolIndex symbols)
    {
        root = Path.GetFullPath(root);
        text = null!;
        symbols = null!;
        try
        {
            var indexPath = IndexStore.IndexPath(root);
            var symbolPath = IndexStore.SymbolIndexPath(root);
            if (!File.Exists(indexPath) || !File.Exists(symbolPath)) return false;
            using (var fs = File.OpenRead(indexPath)) text = TrigramIndex.Load(fs);
            using (var fs = File.OpenRead(symbolPath)) symbols = SymbolIndex.Load(fs);
            return true;
        }
        catch
        {
            // Corrupt or incompatible (older format) index: signal caller to rebuild.
            return false;
        }
    }

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

    private static void SaveAll(string root, TrigramIndex text, SymbolIndex symbols,
                                IReadOnlyDictionary<string, FileState> snapshot)
    {
        using (var fs = File.Create(IndexStore.IndexPath(root))) text.Save(fs);
        using (var fs = File.Create(IndexStore.SymbolIndexPath(root))) symbols.Save(fs);
        using (var fs = File.Create(IndexStore.SnapshotPath(root))) SnapshotStore.Save(fs, snapshot);
    }
}
