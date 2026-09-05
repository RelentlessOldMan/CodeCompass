using System.Text;
using CodeCompass.Core.Ignore;

namespace CodeCompass.Core.Indexing;

/// <summary>A single search hit: 1-based line and column, plus the matched line's text.</summary>
public readonly record struct SearchMatch(string Path, int Line, int Column, string LineText);

/// <summary>
/// Trigram inverted index for fast literal substring search. Each distinct 3-char
/// sequence maps to the sorted list of documents that contain it. A query's trigram
/// posting lists are intersected to get candidate documents, then each candidate is
/// scanned to confirm exact matches ("candidate + verify"). File contents are NOT
/// held in memory - only the postings and paths - so memory stays bounded and the
/// bytes live on disk, the same trade-off zoekt makes.
///
/// Phase 1 scope: literal, case-sensitive substring search. Positional trigrams,
/// regex, and case-folding come later.
/// </summary>
public sealed class TrigramIndex
{
    private const uint Magic = 0x49544343; // "CCTI"
    private const int Version = 1;

    public string RepoRoot { get; private set; } = "";

    private readonly List<string> _docPaths = new();               // docId -> relative path
    private readonly Dictionary<long, List<int>> _postings = new(); // trigram key -> sorted docIds

    public int DocumentCount => _docPaths.Count;
    public int TrigramCount => _postings.Count;

    public static TrigramIndex Build(string repoRoot, IEnumerable<(string relPath, string fullPath)> docs)
    {
        var idx = new TrigramIndex { RepoRoot = Path.GetFullPath(repoRoot) };

        foreach (var (relPath, fullPath) in docs)
        {
            string text;
            try
            {
                var bytes = File.ReadAllBytes(fullPath);
                if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;
                text = Encoding.UTF8.GetString(bytes);
            }
            catch { continue; }

            int docId = idx._docPaths.Count;
            idx._docPaths.Add(relPath);

            foreach (var tri in DistinctTrigrams(text))
            {
                if (!idx._postings.TryGetValue(tri, out var list))
                {
                    list = new List<int>();
                    idx._postings[tri] = list;
                }
                list.Add(docId); // docIds are handed out in increasing order, so the list stays sorted
            }
        }
        return idx;
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
            // Too short to trigram; every document is a candidate. (Rare in practice.)
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
                if (!_postings.TryGetValue(t, out var list)) return results; // a required trigram is absent -> no match
                acc = acc is null ? new List<int>(list) : Intersect(acc, list);
                if (acc.Count == 0) return results;
            }
            candidates = acc ?? Enumerable.Range(0, _docPaths.Count);
        }

        foreach (var docId in candidates)
        {
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
        int line = 1, lineStart = 0, scanned = 0;
        int idx;
        while ((idx = text.IndexOf(query, scanned, StringComparison.Ordinal)) >= 0)
        {
            // advance the running line counter up to the match rather than rescanning from 0
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
            foreach (var d in list) { w.Write(d - prev); prev = d; } // delta-encoded, monotonically increasing
        }
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
        return idx;
    }
}
