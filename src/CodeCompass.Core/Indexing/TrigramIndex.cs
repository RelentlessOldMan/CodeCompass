using System.Text;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Text;

namespace CodeCompass.Core.Indexing;

/// <summary>A single search hit: 1-based line and column, plus the matched line's text.</summary>
public readonly record struct SearchMatch(string Path, int Line, int Column, string LineText);

/// <summary>
/// Trigram inverted index for fast literal substring search. Each distinct 3-char
/// sequence maps to the sorted list of documents that contain it; a query's posting
/// lists are intersected to get candidate documents, which are then scanned to confirm
/// exact matches. File contents are NOT held in memory - only postings and paths.
///
/// Supports incremental updates: replacing or removing a file tombstones its old
/// document id (search skips tombstones) and appends a fresh one. Tombstones are
/// reclaimed by a full rebuild.
/// </summary>
public sealed class TrigramIndex
{
    private const uint Magic = 0x49544343; // "CCTI"
    private const int Version = 2;

    public string RepoRoot { get; private set; } = "";

    private readonly List<string> _docPaths = new();                // docId -> relative path
    private readonly Dictionary<long, List<int>> _postings = new(); // trigram key -> ascending docIds
    private readonly Dictionary<string, int> _pathToDoc = new(StringComparer.Ordinal); // live path -> docId
    private readonly HashSet<int> _deleted = new();                 // tombstoned docIds

    public int DocumentCount => _docPaths.Count - _deleted.Count;
    public int TrigramCount => _postings.Count;

    public static TrigramIndex Create(string repoRoot) =>
        new() { RepoRoot = Path.GetFullPath(repoRoot) };

    public static TrigramIndex Build(string repoRoot, IEnumerable<(string relPath, string fullPath)> docs)
    {
        var idx = Create(repoRoot);
        foreach (var (relPath, fullPath) in docs)
            if (TryReadText(fullPath, out var text))
                idx.AddDocumentText(relPath, text);
        return idx;
    }

    /// <summary>Add a document from already-read text (no file I/O). Caller ensures it isn't binary.</summary>
    public void AddDocumentText(string relPath, string text) =>
        AddDocument(relPath, ComputeTrigrams(text));

    /// <summary>
    /// The distinct trigrams of a text, as a compact array. Pure and thread-safe - the
    /// expensive part of indexing, so it can run lock-free in parallel; the cheap merge
    /// (<see cref="AddDocument(string, long[])"/>) is then serialized by the caller.
    /// </summary>
    public static long[] ComputeTrigrams(string text)
    {
        if (text.Length < 3) return Array.Empty<long>();
        var seen = new HashSet<long>();
        for (int i = 0; i + 3 <= text.Length; i++)
            seen.Add(TriKey(text[i], text[i + 1], text[i + 2]));

        var result = new long[seen.Count];
        seen.CopyTo(result);
        return result;
    }

    /// <summary>
    /// Merge a document's precomputed trigrams into the index. NOT thread-safe: callers
    /// building in parallel must serialize this (docIds are assigned sequentially, which
    /// keeps every posting list sorted).
    /// </summary>
    public void AddDocument(string relPath, long[] distinctTrigrams)
    {
        int docId = _docPaths.Count;
        _docPaths.Add(relPath);
        _pathToDoc[relPath] = docId;

        foreach (var tri in distinctTrigrams)
        {
            if (!_postings.TryGetValue(tri, out var list))
            {
                list = new List<int>();
                _postings[tri] = list;
            }
            list.Add(docId);
        }
    }

    /// <summary>Incrementally replace a file's contribution (tombstone old, add new).</summary>
    public void UpdatePath(string relPath, string fullPath)
    {
        RemovePath(relPath);
        if (TryReadText(fullPath, out var text))
            AddDocumentText(relPath, text);
    }

    public void RemovePath(string relPath)
    {
        if (_pathToDoc.TryGetValue(relPath, out var docId))
        {
            _deleted.Add(docId);
            _pathToDoc.Remove(relPath);
        }
    }

    /// <summary>
    /// Append another (build-only, tombstone-free) index's documents into this one,
    /// remapping its docIds into this index's id space. Called sequentially while merging
    /// per-thread partial indexes; each merged block's ids are strictly greater than all
    /// existing ids, so every posting list stays sorted regardless of merge order.
    /// </summary>
    public void MergeFrom(TrigramIndex other)
    {
        int offset = _docPaths.Count;
        for (int local = 0; local < other._docPaths.Count; local++)
        {
            var rel = other._docPaths[local];
            _docPaths.Add(rel);
            _pathToDoc[rel] = offset + local;
        }
        foreach (var (key, list) in other._postings)
        {
            if (!_postings.TryGetValue(key, out var dest))
            {
                dest = new List<int>(list.Count);
                _postings[key] = dest;
            }
            foreach (var localId in list) dest.Add(offset + localId);
        }
    }

    private static bool TryReadText(string fullPath, out string text)
    {
        text = "";
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) return false;
            text = TextDecoder.FromBytes(bytes);
            return true;
        }
        catch { return false; }
    }

    private static long TriKey(char a, char b, char c) => ((long)a << 32) | ((long)b << 16) | c;

    private static IEnumerable<long> DistinctTrigrams(string text)
    {
        if (text.Length < 3) yield break;
        var seen = new HashSet<long>();
        for (int i = 0; i + 3 <= text.Length; i++)
        {
            var key = TriKey(text[i], text[i + 1], text[i + 2]);
            if (seen.Add(key)) yield return key;
        }
    }

    public IReadOnlyList<SearchMatch> Search(string query, int maxResults = 200)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query)) return results;

        IEnumerable<int> candidates;
        if (query.Length < 3)
        {
            candidates = Enumerable.Range(0, _docPaths.Count);
        }
        else
        {
            var tris = new List<long>();
            var seen = new HashSet<long>();
            for (int i = 0; i + 3 <= query.Length; i++)
            {
                var k = TriKey(query[i], query[i + 1], query[i + 2]);
                if (seen.Add(k)) tris.Add(k);
            }

            List<int>? acc = null;
            foreach (var t in tris)
            {
                if (!_postings.TryGetValue(t, out var list)) return results; // required trigram absent
                acc = acc is null ? new List<int>(list) : Intersect(acc, list);
                if (acc.Count == 0) return results;
            }
            candidates = acc ?? Enumerable.Range(0, _docPaths.Count);
        }

        foreach (var docId in candidates)
        {
            if (_deleted.Contains(docId)) continue; // skip tombstones
            var rel = _docPaths[docId];
            var full = Path.Combine(RepoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            string text;
            try { text = File.ReadAllText(full); }
            catch { continue; }

            ScanFile(rel, text, query, results, maxResults);
            if (results.Count >= maxResults) break;
        }
        return results;
    }

    private static List<int> Intersect(List<int> a, List<int> b)
    {
        var result = new List<int>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i] == b[j]) { result.Add(a[i]); i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }
        return result;
    }

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

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(RepoRoot);

        w.Write(_docPaths.Count);
        foreach (var p in _docPaths) w.Write(p);

        w.Write(_postings.Count);
        foreach (var (key, list) in _postings)
        {
            w.Write(key);
            w.Write(list.Count);
            int prev = 0;
            foreach (var d in list) { w.Write(d - prev); prev = d; }
        }

        w.Write(_deleted.Count);
        foreach (var d in _deleted) w.Write(d);
    }

    public static TrigramIndex Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a CodeCompass index file");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"unsupported index version {version}");

        var idx = new TrigramIndex { RepoRoot = r.ReadString() };

        int docCount = r.ReadInt32();
        idx._docPaths.Capacity = docCount;
        for (int i = 0; i < docCount; i++) idx._docPaths.Add(r.ReadString());

        int triCount = r.ReadInt32();
        for (int i = 0; i < triCount; i++)
        {
            long key = r.ReadInt64();
            int n = r.ReadInt32();
            var list = new List<int>(n);
            int prev = 0;
            for (int j = 0; j < n; j++) { prev += r.ReadInt32(); list.Add(prev); }
            idx._postings[key] = list;
        }

        int delCount = r.ReadInt32();
        for (int i = 0; i < delCount; i++) idx._deleted.Add(r.ReadInt32());

        // Rebuild live path -> docId (ascending, so the newest live doc for a path wins).
        for (int docId = 0; docId < idx._docPaths.Count; docId++)
            if (!idx._deleted.Contains(docId))
                idx._pathToDoc[idx._docPaths[docId]] = docId;

        return idx;
    }
}
