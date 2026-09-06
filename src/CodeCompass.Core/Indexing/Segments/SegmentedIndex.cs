using CodeCompass.Core.Indexing;

namespace CodeCompass.Core.Indexing.Segments;

/// <summary>
/// A trigram index made of many immutable on-disk segments plus a small mutable set of
/// tombstones. Memory stays bounded: building flushes a segment whenever the in-memory
/// buffer hits a budget, and searching memory-maps segments and reads only the posting
/// lists a query needs. A changed file tombstones its old doc and appends a new one.
///
/// Segment files use a monotonic, never-reused number (persisted in the manifest) and are
/// never overwritten, so a rebuild can write a fresh set while old readers still hold the
/// previous segments' memory maps (important on Windows, where mapped files can't be
/// deleted). Orphaned segments not in the current manifest are cleaned up best-effort.
///
/// Contract: call <see cref="Flush"/> after a batch of adds/removes before searching or
/// persisting. Search materializes any pending buffer first, so results are always current.
/// </summary>
public sealed class SegmentedIndex : IDisposable
{
    public const long DefaultBudgetBytes = 64L * 1024 * 1024;
    private const string ManifestName = "segments.manifest";
    private const string TombstoneName = "segments.tombstones";
    private const string SegmentPattern = "seg-*.ccseg";

    private readonly string _root;
    private readonly string _dir;
    private readonly long _budget;
    private readonly List<SegmentReader> _segments = new();
    private readonly Dictionary<int, HashSet<int>> _tombstones = new();
    private readonly Dictionary<string, (int Seg, int Local)> _pathToDoc = new(StringComparer.Ordinal);
    private SegmentBuilder? _pending;
    private int _nextSegmentNumber;
    private bool _pathMapBuilt;

    public string Root => _root;
    public string Directory => _dir;
    public int SegmentCount => _segments.Count;

    private SegmentedIndex(string root, string dir, long budget)
    {
        _root = Path.GetFullPath(root);
        _dir = dir;
        _budget = budget;
        System.IO.Directory.CreateDirectory(dir);
    }

    /// <summary>Fresh empty index; new segments get numbers past any existing (possibly locked) files.</summary>
    public static SegmentedIndex Create(string root, string dir, long budget = DefaultBudgetBytes)
    {
        var idx = new SegmentedIndex(root, dir, budget);
        idx._nextSegmentNumber = NextSegmentNumber(dir);
        return idx;
    }

    /// <summary>Open an existing index (manifest + segments + tombstones).</summary>
    public static SegmentedIndex Open(string root, string dir, long budget = DefaultBudgetBytes)
    {
        var idx = new SegmentedIndex(root, dir, budget);
        idx.Load();
        return idx;
    }

    /// <summary>Assemble an index from segment files written out-of-band (e.g. a parallel build).</summary>
    public static SegmentedIndex FromSegmentFiles(
        string root, string dir, IReadOnlyList<string> segmentFileNames, int nextSegmentNumber,
        long budget = DefaultBudgetBytes)
    {
        var idx = new SegmentedIndex(root, dir, budget) { _nextSegmentNumber = nextSegmentNumber };
        foreach (var name in segmentFileNames)
            idx._segments.Add(new SegmentReader(Path.Combine(dir, name)));
        idx.SaveManifest();
        idx.SaveTombstones();
        idx.CleanupOrphans();
        return idx; // path map built lazily on first mutation (search-only never pays for it)
    }

    /// <summary>The next never-before-used segment number for a directory (max existing + 1).</summary>
    public static int NextSegmentNumber(string dir)
    {
        int max = -1;
        if (System.IO.Directory.Exists(dir))
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, SegmentPattern))
            {
                var name = Path.GetFileNameWithoutExtension(f); // "seg-00000123"
                if (name.Length > 4 && int.TryParse(name.AsSpan(4), out var n) && n > max) max = n;
            }
        }
        return max + 1;
    }

    public static string SegmentFileName(int number) => $"seg-{number:D8}.ccseg";

    /// <summary>True if an index (manifest) already exists in the directory.</summary>
    public static bool Exists(string dir) => File.Exists(Path.Combine(dir, ManifestName));

    public int DocumentCount
    {
        get
        {
            int n = _pending?.DocCount ?? 0;
            for (int s = 0; s < _segments.Count; s++)
                n += _segments[s].DocCount - (_tombstones.TryGetValue(s, out var t) ? t.Count : 0);
            return n;
        }
    }

    public long TotalTerms
    {
        get { long n = 0; foreach (var s in _segments) n += s.TermCount; return n; }
    }

    public long IndexBytes
    {
        get
        {
            long n = 0;
            foreach (var s in _segments)
                try { n += new FileInfo(s.FilePath).Length; } catch { /* ignore */ }
            return n;
        }
    }

    public void AddDocumentText(string relPath, string text) =>
        AddDocument(relPath, TrigramIndex.ComputeTrigrams(text));

    public void AddDocument(string relPath, long[] distinctTrigrams)
    {
        EnsurePathMap();
        _pending ??= new SegmentBuilder();
        int local = _pending.DocCount;
        _pending.AddDocument(relPath, distinctTrigrams);
        _pathToDoc[relPath] = (_segments.Count, local); // pending's future segment index
        if (_pending.ApproxBytes >= _budget) FlushPending();
    }

    public void RemovePath(string relPath)
    {
        EnsurePathMap();
        if (!_pathToDoc.TryGetValue(relPath, out var loc)) return;
        if (loc.Seg == _segments.Count && _pending is not null) FlushPending(); // materialize so we can tombstone
        if (!_tombstones.TryGetValue(loc.Seg, out var set))
        {
            set = new HashSet<int>();
            _tombstones[loc.Seg] = set;
        }
        set.Add(loc.Local);
        _pathToDoc.Remove(relPath);
    }

    public IReadOnlyList<SearchMatch> Search(string query, int maxResults = 200)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query)) return results;
        if (_pending is { DocCount: > 0 }) FlushPending();

        long[] tris = query.Length >= 3 ? TrigramIndex.ComputeTrigrams(query) : System.Array.Empty<long>();

        for (int segId = 0; segId < _segments.Count; segId++)
        {
            var seg = _segments[segId];
            _tombstones.TryGetValue(segId, out var tomb);

            IEnumerable<int> candidates;
            if (tris.Length == 0)
            {
                candidates = Enumerable.Range(0, seg.DocCount);
            }
            else
            {
                List<int>? acc = null;
                bool absent = false;
                foreach (var t in tris)
                {
                    var postings = seg.GetPostings(t);
                    if (postings is null) { absent = true; break; }
                    acc = acc is null ? new List<int>(postings) : Intersect(acc, postings);
                    if (acc.Count == 0) { absent = true; break; }
                }
                if (absent || acc is null) continue;
                candidates = acc;
            }

            foreach (var local in candidates)
            {
                if (tomb is not null && tomb.Contains(local)) continue;
                var rel = seg.GetPath(local);
                var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
                string text;
                try { text = File.ReadAllText(full); }
                catch { continue; }
                ScanFile(rel, text, query, results, maxResults);
                if (results.Count >= maxResults) return results;
            }
        }
        return results;
    }

    /// <summary>Flush the pending buffer to a segment and persist manifest + tombstones.</summary>
    public void Flush()
    {
        FlushPending();
        SaveManifest();
        SaveTombstones();
        CleanupOrphans();
    }

    private void FlushPending()
    {
        if (_pending is null || _pending.DocCount == 0) { _pending = null; return; }
        var file = Path.Combine(_dir, SegmentFileName(_nextSegmentNumber));
        _nextSegmentNumber++;
        _pending.WriteTo(file);
        _pending = null;
        _segments.Add(new SegmentReader(file));
    }

    private static List<int> Intersect(List<int> a, int[] b)
    {
        var result = new List<int>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Length)
        {
            if (a[i] == b[j]) { result.Add(a[i]); i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }
        return result;
    }

    // Mirrors TrigramIndex.ScanFile exactly so results/positions are identical.
    private static void ScanFile(string rel, string text, string query, List<SearchMatch> results, int maxResults)
    {
        int line = 1, lineStart = 0, scanned = 0, idx;
        while ((idx = text.IndexOf(query, scanned, StringComparison.Ordinal)) >= 0)
        {
            for (int k = scanned; k < idx; k++)
                if (text[k] == '\n') { line++; lineStart = k + 1; }
            int lineEnd = text.IndexOf('\n', idx);
            if (lineEnd < 0) lineEnd = text.Length;
            var lineText = text.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');
            results.Add(new SearchMatch(rel, line, idx - lineStart + 1, lineText));
            if (results.Count >= maxResults) return;
            scanned = idx + query.Length;
        }
    }

    private void SaveManifest()
    {
        using var w = new StreamWriter(Path.Combine(_dir, ManifestName), append: false);
        w.WriteLine(_root);
        w.WriteLine(_nextSegmentNumber);
        foreach (var s in _segments)
            w.WriteLine(Path.GetFileName(s.FilePath));
    }

    private void SaveTombstones()
    {
        using var fs = File.Create(Path.Combine(_dir, TombstoneName));
        using var w = new BinaryWriter(fs);
        w.Write(_tombstones.Count);
        foreach (var (seg, locals) in _tombstones)
        {
            w.Write(seg);
            w.Write(locals.Count);
            foreach (var l in locals) w.Write(l);
        }
    }

    private void CleanupOrphans()
    {
        var live = new HashSet<string>(
            _segments.Select(s => Path.GetFileName(s.FilePath)), StringComparer.OrdinalIgnoreCase);
        foreach (var f in System.IO.Directory.EnumerateFiles(_dir, SegmentPattern))
            if (!live.Contains(Path.GetFileName(f)))
                try { File.Delete(f); } catch { /* still mapped by another reader: leave it */ }
    }

    private void Load()
    {
        var manifestPath = Path.Combine(_dir, ManifestName);
        if (!File.Exists(manifestPath)) return;

        var lines = File.ReadAllLines(manifestPath);
        if (lines.Length >= 2) int.TryParse(lines[1], out _nextSegmentNumber);
        for (int i = 2; i < lines.Length; i++)
        {
            var file = Path.Combine(_dir, lines[i]);
            if (File.Exists(file)) _segments.Add(new SegmentReader(file));
        }
        if (_nextSegmentNumber < _segments.Count) _nextSegmentNumber = NextSegmentNumber(_dir);

        var tombPath = Path.Combine(_dir, TombstoneName);
        if (File.Exists(tombPath))
        {
            using var fs = File.OpenRead(tombPath);
            using var r = new BinaryReader(fs);
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int seg = r.ReadInt32();
                int n = r.ReadInt32();
                var set = new HashSet<int>(n);
                for (int j = 0; j < n; j++) set.Add(r.ReadInt32());
                _tombstones[seg] = set;
            }
        }
        // path map is built lazily (see EnsurePathMap) so a search-only open stays cheap
    }

    // Live path -> location, needed only for RemovePath/incremental. Built once, on demand;
    // O(docs) but reads paths only (mmap), not postings. Search never triggers it.
    public void EnsurePathMap()
    {
        if (_pathMapBuilt) return;
        _pathMapBuilt = true;
        for (int segId = 0; segId < _segments.Count; segId++)
        {
            _tombstones.TryGetValue(segId, out var tomb);
            var seg = _segments[segId];
            for (int local = 0; local < seg.DocCount; local++)
            {
                if (tomb is not null && tomb.Contains(local)) continue;
                _pathToDoc[seg.GetPath(local)] = (segId, local); // later segments win on duplicate paths
            }
        }
    }

    public void Dispose()
    {
        foreach (var s in _segments) s.Dispose();
        _segments.Clear();
    }
}
