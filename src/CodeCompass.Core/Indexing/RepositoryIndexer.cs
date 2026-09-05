using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Indexing;

public sealed record IndexStats(int Files, long Bytes, int Trigrams, int Symbols, double Seconds, long IndexBytes);

public sealed record UpdateStats(int Added, int Modified, int Removed, double Seconds, bool FullRebuild);

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
        var sw = Stopwatch.StartNew();

        var text = TrigramIndex.Create(root);
        var symbolList = new List<Symbol>();
        var snapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
        long totalBytes = 0;

        using (var extractor = new TreeSitterSymbolExtractor())
        {
            foreach (var file in walker.Walk(root))
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file.FullPath); }
                catch { continue; }

                if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;

                var content = Encoding.UTF8.GetString(bytes);
                var mtime = File.GetLastWriteTimeUtc(file.FullPath).Ticks;
                var hash = Convert.ToHexString(SHA256.HashData(bytes));

                totalBytes += bytes.Length;
                snapshot[file.RelativePath] = new FileState(bytes.Length, mtime, hash);
                text.AddDocumentText(file.RelativePath, content);
                if (LanguageRegistry.ForPath(file.RelativePath) is not null)
                    symbolList.AddRange(extractor.Extract(file.RelativePath, content));
            }
        }

        var symbols = SymbolIndex.Build(symbolList);
        sw.Stop();

        SaveAll(root, text, symbols, snapshot);

        long indexBytes = new FileInfo(IndexStore.IndexPath(root)).Length;
        var stats = new IndexStats(text.DocumentCount, totalBytes, text.TrigramCount,
                                   symbols.Count, sw.Elapsed.TotalSeconds, indexBytes);
        return (text, symbols, stats);
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

                var content = Encoding.UTF8.GetString(bytes);
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
