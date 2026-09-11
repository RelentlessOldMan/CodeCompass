namespace CodeCompass.Core.Indexing;

/// <summary>
/// Turns a trigram candidate file into concrete <see cref="SearchMatch"/> hits (line:col + the
/// matching line's text). The trigram index only says a file *might* contain the query; this
/// confirms it and locates it. Two entry points with identical results for single-line queries:
/// <see cref="ScanText"/> over an in-memory string (fast, for normal files) and
/// <see cref="ScanByLine"/> which streams the file line by line (bounded memory, for very large
/// files that cannot be held as one string).
/// </summary>
public static class FileScanner
{
    /// <summary>Scan an already-loaded file body. Finds every occurrence (Ordinal), tracking line
    /// and column, up to maxResults.</summary>
    /// <param name="lineOffset">Added to reported line numbers. Used when scanning a block that starts
    /// partway into a file (the block's first line is file line <c>lineOffset + 1</c>).</param>
    public static void ScanText(string rel, string text, string query, List<SearchMatch> results, int maxResults, int lineOffset = 0)
    {
        int line = 1, lineStart = 0, scanned = 0, idx;
        while ((idx = text.IndexOf(query, scanned, StringComparison.Ordinal)) >= 0)
        {
            for (int k = scanned; k < idx; k++)
                if (text[k] == '\n') { line++; lineStart = k + 1; }

            int lineEnd = text.IndexOf('\n', idx);
            if (lineEnd < 0) lineEnd = text.Length;
            var lineText = text.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');

            results.Add(new SearchMatch(rel, line + lineOffset, idx - lineStart + 1, lineText));
            if (results.Count >= maxResults) return;
            scanned = idx + Math.Max(1, query.Length);
        }
    }

    /// <summary>Stream a file line by line and find the query within each line. Bounded memory, so it
    /// works on files far larger than a single .NET string can hold. Matches <see cref="ScanText"/>
    /// for single-line queries; a query containing a newline won't be found by this path (rare, and
    /// only affects files large enough to require streaming).</summary>
    public static void ScanByLine(string rel, string fullPath, string query, List<SearchMatch> results, int maxResults)
    {
        int line = 0;
        IEnumerable<string> lines;
        try { lines = File.ReadLines(fullPath); } // streams; honors BOM/encoding like ReadAllText
        catch { return; }
        foreach (var raw in lines)
        {
            line++;
            var lineText = raw.TrimEnd('\r');
            int from = 0, idx;
            while ((idx = lineText.IndexOf(query, from, StringComparison.Ordinal)) >= 0)
            {
                results.Add(new SearchMatch(rel, line, idx + 1, lineText));
                if (results.Count >= maxResults) return;
                from = idx + Math.Max(1, query.Length);
            }
        }
    }
}
