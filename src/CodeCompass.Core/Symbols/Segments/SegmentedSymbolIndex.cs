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
        var idx = new SegmentedSymbolIndex(dir, budget) { _nextSegmentNumber = nextSegmentNumber };
        foreach (var name in segmentFileNames)
            idx._segments.Add(new SymbolSegmentReader(Path.Combine(dir, name)));
        idx.SaveManifest();
        idx.SaveTombstones();
        idx.CleanupOrphans();
        return idx;
    }

    public static int NextSegmentNumber(string dir) => NumberedFiles.Next(dir, SegmentPattern);

    public static string SegmentFileName(int number) => $"sym-{number:D8}.ccsym";

    public static bool Exists(string dir) => File.Exists(Path.Combine(dir, ManifestName));

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
        CleanupOrphans();
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

    private void SaveManifest() => AtomicFile.WriteText(Path.Combine(_dir, ManifestName), w =>
    {
        w.WriteLine(_nextSegmentNumber);
        foreach (var s in _segments) w.WriteLine(Path.GetFileName(s.FilePath));
    });

    private void SaveTombstones() => AtomicFile.Write(Path.Combine(_dir, TombstoneName), fs =>
    {
        using var w = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(_tombstones.Count);
        foreach (var (seg, paths) in _tombstones)
        {
            w.Write(seg);
            w.Write(paths.Count);
            foreach (var p in paths) w.Write(p);
        }
    });

    private void CleanupOrphans() => NumberedFiles.CleanupOrphans(_dir, SegmentPattern,
        new HashSet<string>(_segments.Select(s => Path.GetFileName(s.FilePath)), StringComparer.OrdinalIgnoreCase));

    private void Load()
    {
        var manifestPath = Path.Combine(_dir, ManifestName);
        if (!File.Exists(manifestPath)) return;

        var lines = File.ReadAllLines(manifestPath);
        if (lines.Length >= 1) int.TryParse(lines[0], out _nextSegmentNumber);
        for (int i = 1; i < lines.Length; i++)
        {
            if (!PathSafety.IsBareFileName(lines[i])) continue; // a tampered manifest can't point outside _dir
            var file = Path.Combine(_dir, lines[i]);
            if (File.Exists(file)) _segments.Add(new SymbolSegmentReader(file));
        }
        if (_nextSegmentNumber < _segments.Count) _nextSegmentNumber = NextSegmentNumber(_dir);

        var tombPath = Path.Combine(_dir, TombstoneName);
        if (File.Exists(tombPath))
        {
            using var fs = File.OpenRead(tombPath);
            using var r = new BinaryReader(fs, System.Text.Encoding.UTF8);
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int seg = r.ReadInt32();
                int n = r.ReadInt32();
                var set = new HashSet<string>(n, StringComparer.Ordinal);
                for (int j = 0; j < n; j++) set.Add(r.ReadString());
                _tombstones[seg] = set;
            }
        }
    }

    public void Dispose()
    {
        foreach (var s in _segments) s.Dispose();
        _segments.Clear();
    }
}
