using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Storage;

namespace CodeCompass.Core.Symbols.Segments;

/// <summary>
/// A symbol index made of many immutable, name-sorted, memory-mapped segments plus
/// path-level tombstones. Keeps symbol memory bounded: build flushes a segment at a byte
/// budget, and lookups read only what they touch via mmap. A changed file's old symbols
/// are tombstoned by path (in the segments that contained it) and its new symbols are
/// appended in a fresh segment.
///
/// Mirrors <see cref="CodeCompass.Core.Indexing.Segments.SegmentedIndex"/>: monotonic,
/// never-overwrite segment numbering so a rebuild never fights readers' memory maps.
/// Call <see cref="Flush"/> after a batch before searching or persisting.
/// </summary>
public sealed class SegmentedSymbolIndex : IDisposable
{
    public const long DefaultBudgetBytes = 32L * 1024 * 1024;
    private const string ManifestName = "symbols.manifest";
    private const string TombstoneName = "symbols.tombstones";
    private const string SegmentPattern = "sym-*.ccsym";

    private readonly string _dir;
    private readonly long _budget;
    private readonly List<SymbolSegmentReader> _segments = new();
    private readonly Dictionary<int, HashSet<string>> _tombstones = new(); // segId -> tombstoned paths
    private SymbolSegmentBuilder? _pending;
    private int _nextSegmentNumber;

    public string Directory => _dir;
    public int SegmentCount => _segments.Count;

    private SegmentedSymbolIndex(string dir, long budget)
    {
        _dir = dir;
        _budget = budget;
        System.IO.Directory.CreateDirectory(dir);
    }

    public static SegmentedSymbolIndex Create(string dir, long budget = DefaultBudgetBytes)
    {
        var idx = new SegmentedSymbolIndex(dir, budget);
        idx._nextSegmentNumber = NextSegmentNumber(dir);
        return idx;
    }

    public static SegmentedSymbolIndex Open(string dir, long budget = DefaultBudgetBytes)
    {
        var idx = new SegmentedSymbolIndex(dir, budget);
        idx.Load();
        return idx;
    }

    public static SegmentedSymbolIndex FromSegmentFiles(
        string dir, IReadOnlyList<string> segmentFileNames, int nextSegmentNumber, long budget = DefaultBudgetBytes)
    {
        // Clamp against the on-disk max: a number below an existing sym-*.ccsym would make the next flush reuse
        // a live segment's filename and overwrite it, breaking the monotonic never-reuse invariant. (Mirrors Load.)
        var idx = new SegmentedSymbolIndex(dir, budget) { _nextSegmentNumber = Math.Max(nextSegmentNumber, NextSegmentNumber(dir)) };
        foreach (var name in segmentFileNames)
            idx._segments.Add(new SymbolSegmentReader(Path.Combine(dir, name)));
        idx.SaveManifest();
        idx.SaveTombstones();
        idx.CleanupOrphans();
        return idx;
    }

    public static int NextSegmentNumber(string dir) => NumberedFiles.Next(dir, SegmentPattern);

    public static string SegmentFileName(int number) => $"sym-{number:D8}.ccsym";

    /// <summary>
    /// True if a loadable symbol index exists. Mirrors <see cref="CodeCompass.Core.Indexing.Segments.SegmentedIndex.Exists"/>:
    /// a lost manifest with surviving sym-*.ccsym segments is still recoverable by Load, so Exists must agree, or
    /// TryLoad's gate bypasses recovery. Manifest first (cheap common path); enumerate only when it's missing.
    /// </summary>
    public static bool Exists(string dir) =>
        File.Exists(Path.Combine(dir, ManifestName)) ||
        (System.IO.Directory.Exists(dir) && System.IO.Directory.EnumerateFiles(dir, SegmentPattern).Any());

    /// <summary>Raw symbol count (includes not-yet-compacted tombstoned symbols); exact after a build.</summary>
    public int Count
    {
        get { int n = _pending?.Count ?? 0; foreach (var s in _segments) n += s.Count; return n; }
    }

    public void Add(Symbol symbol)
    {
        _pending ??= new SymbolSegmentBuilder();
        _pending.Add(symbol);
        if (_pending.ApproxBytes >= _budget) FlushPending();
    }

    public void AddForPath(string relativePath, IEnumerable<Symbol> symbols)
    {
        foreach (var s in symbols) Add(s);
    }

    public void RemovePath(string relativePath)
    {
        for (int segId = 0; segId < _segments.Count; segId++)
        {
            if (!_segments[segId].ContainsPath(relativePath)) continue;
            if (!_tombstones.TryGetValue(segId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _tombstones[segId] = set;
            }
            set.Add(relativePath);
        }
    }

    /// <summary>Every live symbol-bearing path in the index (deduped, tombstoned paths excluded). Used by the
    /// stale-index ignore prune to find paths the CURRENT rules now exclude. O(symbols); no source reads.</summary>
    public IReadOnlyCollection<string> AllPaths()
    {
        if (_pending is { Count: > 0 }) FlushPending();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (int segId = 0; segId < _segments.Count; segId++)
        {
            var seg = _segments[segId];
            _tombstones.TryGetValue(segId, out var tomb);
            for (int i = 0; i < seg.Count; i++)
            {
                var p = seg.GetSymbolPath(i);
                if (tomb is not null && tomb.Contains(p)) continue;
                paths.Add(p);
            }
        }
        return paths;
    }

    public IReadOnlyList<Symbol> FindByName(string name)
    {
        if (_pending is { Count: > 0 }) FlushPending();
        var result = new List<Symbol>();
        for (int segId = 0; segId < _segments.Count; segId++)
        {
            var seg = _segments[segId];
            _tombstones.TryGetValue(segId, out var tomb);
            foreach (var i in seg.FindByName(name))
            {
                if (tomb is not null && tomb.Contains(seg.GetSymbolPath(i))) continue;
                // Query-time ignore consistency: drop symbols a stale index still holds for now-ignored paths.
                if (CodeCompass.Core.Ignore.IgnoreRules.QueryDefault.IsIgnoredPath(seg.GetSymbolPath(i))) continue;
                result.Add(seg.GetSymbol(i));
            }
        }
        return result;
    }

    public IReadOnlyList<Symbol> Find(string queryText, int max = 200)
    {
        if (_pending is { Count: > 0 }) FlushPending();
        var result = new List<Symbol>();
        for (int segId = 0; segId < _segments.Count; segId++)
        {
            var seg = _segments[segId];
            _tombstones.TryGetValue(segId, out var tomb);
            for (int i = 0; i < seg.Count; i++)
            {
                if (!seg.GetName(i).Contains(queryText, StringComparison.OrdinalIgnoreCase)) continue;
                if (tomb is not null && tomb.Contains(seg.GetSymbolPath(i))) continue;
                if (CodeCompass.Core.Ignore.IgnoreRules.QueryDefault.IsIgnoredPath(seg.GetSymbolPath(i))) continue;
                result.Add(seg.GetSymbol(i));
                if (result.Count >= max) return result;
            }
        }
        return result;
    }

    public void Flush()
    {
        FlushPending();
        SaveManifest();
        SaveTombstones();
        // No CleanupOrphans() here (see SegmentedIndex.Flush): Flush only appends a new numbered segment and
        // never orphans a file; orphan cleanup happens in Compact and full rebuild, which are the only paths
        // that drop segments. Avoids an O(files-in-cache) directory scan on every incremental save.
    }

    /// <summary>
    /// Merge all segments into a fresh, tombstone-free set, dropping tombstoned paths' symbols.
    /// Bounded memory: symbols are streamed from each segment into a budget-flushed builder (no
    /// source files re-read). New segments use fresh monotonic numbers so old maps stay valid.
    /// </summary>
    public void Compact()
    {
        FlushPending();
        if (_segments.Count <= 1 && _tombstones.Count == 0) return; // already compact

        var old = _segments.ToList();
        var merged = new List<SymbolSegmentReader>();
        var builder = new SymbolSegmentBuilder();

        void FlushMerged()
        {
            if (builder.Count == 0) return;
            var file = Path.Combine(_dir, SegmentFileName(_nextSegmentNumber));
            _nextSegmentNumber++;
            builder.WriteTo(file);
            merged.Add(new SymbolSegmentReader(file));
            builder = new SymbolSegmentBuilder();
        }

        for (int segId = 0; segId < old.Count; segId++)
        {
            var seg = old[segId];
            _tombstones.TryGetValue(segId, out var tomb);
            for (int i = 0; i < seg.Count; i++)
            {
                if (tomb is not null && tomb.Contains(seg.GetSymbolPath(i))) continue;
                builder.Add(seg.GetSymbol(i));
                if (builder.ApproxBytes >= _budget) FlushMerged();
            }
        }
        FlushMerged();

        foreach (var s in old) s.Dispose();
        _segments.Clear();
        _segments.AddRange(merged);
        _tombstones.Clear();

        SaveManifest();
        SaveTombstones();
        CleanupOrphans();
    }

    private void FlushPending()
    {
        if (_pending is null || _pending.Count == 0) { _pending = null; return; }
        var file = Path.Combine(_dir, SegmentFileName(_nextSegmentNumber));
        _nextSegmentNumber++;
        _pending.WriteTo(file);
        _pending = null;
        _segments.Add(new SymbolSegmentReader(file));
    }

    // Tombstones ride in the manifest (one atomic commit with the segment list) - see SegmentedIndex.SaveManifest.
    private const string ManifestTombstonePrefix = "#T ";

    private void SaveManifest() => AtomicFile.WriteText(Path.Combine(_dir, ManifestName), w =>
    {
        w.WriteLine(_nextSegmentNumber);
        foreach (var s in _segments) w.WriteLine(Path.GetFileName(s.FilePath));
        w.WriteLine(ManifestTombstonePrefix + Convert.ToBase64String(TombstoneBytes()));
    });

    private void SaveTombstones() => AtomicFile.Write(Path.Combine(_dir, TombstoneName), fs => fs.Write(TombstoneBytes()));

    private byte[] TombstoneBytes()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(_tombstones.Count);
            foreach (var (seg, paths) in _tombstones)
            {
                w.Write(seg);
                w.Write(paths.Count);
                foreach (var p in paths) w.Write(p);
            }
        }
        return ms.ToArray();
    }

    private void ReadTombstones(Stream s)
    {
        using var r = new BinaryReader(s, System.Text.Encoding.UTF8);
        int count = r.ReadInt32();
        if (count < 0) throw new InvalidDataException($"corrupt symbol tombstone file: negative segment count ({count})");
        for (int i = 0; i < count; i++)
        {
            int seg = r.ReadInt32();
            int n = r.ReadInt32();
            if (n < 0) throw new InvalidDataException($"corrupt symbol tombstone file: negative entry count ({n})");
            // Cap the pre-size: an untrusted `n` must not force a huge allocation before any path is read.
            // The set still grows to fit; an `n` that overruns the file throws EndOfStream. (Mirrors
            // SegmentedIndex.Load.)
            var set = new HashSet<string>(Math.Min(n, 4096), StringComparer.Ordinal);
            for (int j = 0; j < n; j++) set.Add(r.ReadString());
            _tombstones[seg] = set;
        }
    }

    private void CleanupOrphans() => NumberedFiles.CleanupOrphans(_dir, SegmentPattern,
        new HashSet<string>(_segments.Select(s => Path.GetFileName(s.FilePath)), StringComparer.OrdinalIgnoreCase));

    // See SegmentedIndex.Load: a manifest naming a vanished segment is re-read; only a persistent gap is corruption.
    private void Load()
    {
        for (int attempt = 1; ; attempt++)
        {
            try { LoadOnce(); return; }
            catch (MissingSegmentException) when (attempt < 3)
            {
                foreach (var s in _segments) s.Dispose();
                _segments.Clear(); _tombstones.Clear(); _nextSegmentNumber = 0;
                Thread.Sleep(50 * attempt);
            }
            catch (MissingSegmentException ex)
            {
                foreach (var s in _segments) s.Dispose();
                _segments.Clear();
                throw new InvalidDataException(ex.Message);
            }
        }
    }

    private sealed class MissingSegmentException(string message) : Exception(message);

    private void LoadOnce()
    {
        var manifestPath = Path.Combine(_dir, ManifestName);
        byte[]? manifestTombstones = null;
        if (File.Exists(manifestPath))
        {
            var lines = AtomicFile.ReadWithRetry(manifestPath, File.ReadAllLines);
            if (lines.Length >= 1) int.TryParse(lines[0], out _nextSegmentNumber);
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(ManifestTombstonePrefix, StringComparison.Ordinal))
                {
                    manifestTombstones = Convert.FromBase64String(lines[i][ManifestTombstonePrefix.Length..]);
                    continue;
                }
                if (!PathSafety.IsBareFileName(lines[i])) continue; // a tampered manifest can't point outside _dir
                var file = Path.Combine(_dir, lines[i]);
                if (!File.Exists(file)) throw new MissingSegmentException($"symbol index manifest names a missing segment: {lines[i]}");
                _segments.Add(new SymbolSegmentReader(file));
            }
        }

        // Durability net (mirrors SegmentedIndex.Load): reconstruct from sym-*.ccsym files if the manifest is
        // missing/empty but segments remain, rather than treating the symbol index as EMPTY (silent total loss).
        if (_segments.Count == 0 && System.IO.Directory.Exists(_dir))
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir, SegmentPattern).OrderBy(f => f, StringComparer.Ordinal))
            {
                try { _segments.Add(new SymbolSegmentReader(f)); }
                catch (Exception ex) { Log.Global.Warn($"skipping unreadable symbol segment {Path.GetFileName(f)} during manifest-less recovery: {ex.Message}"); }
            }
            if (_segments.Count > 0)
                Log.Global.Warn($"symbol index manifest missing/empty at {_dir}; reconstructed {_segments.Count} segment(s) from disk (run codecompass index to restore tombstones/compaction)");
        }

        // Never below a number already on disk (see SegmentedIndex.LoadOnce).
        _nextSegmentNumber = Math.Max(_nextSegmentNumber, NextSegmentNumber(_dir));

        if (manifestTombstones is not null) { ReadTombstones(new MemoryStream(manifestTombstones)); return; }
        var tombPath = Path.Combine(_dir, TombstoneName); // a manifest written before tombstones moved into it
        if (File.Exists(tombPath))
            ReadTombstones(new MemoryStream(AtomicFile.ReadWithRetry(tombPath, File.ReadAllBytes)));
    }

    public void Dispose()
    {
        foreach (var s in _segments) s.Dispose();
        _segments.Clear();
    }
}
