using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeCompass.Semantics;

/// <summary>
/// Classifies, per file, the character ranges that are COMMENTS or STRING/CHAR literals, so the lexical
/// reference name matching can skip whole-word hits that fall inside them - an XML-doc <c>&lt;see cref="X"/&gt;</c>
/// mention or an <c>"X"</c> in a string literal is not a use of X, and find_references promises to ignore comments and
/// strings. Applied to C# (where it guards the backfill of what Roslyn couldn't see) and to C/C++ (whose references
/// are matched by name). For C# it uses Roslyn's own lexer (exact: single-/multi-line comments, <c>///</c> doc
/// comments, verbatim/interpolated/raw/utf8 strings, char literals - and it leaves interpolation holes
/// <c>{expr}</c> as code, so a reference inside one is kept); for C/C++ a conservative C-family scanner.
///
/// <para>Bias on ANY uncertainty (parse/read failure, a construct the scanner doesn't model): DO NOT suppress.
/// Keeping a stray comment/string hit is cheaper than dropping a real reference - find_references must never go
/// quiet on a genuine use. Spans are computed once per path and cached for the lifetime of a single query.</para>
/// </summary>
public sealed class LexicalSpanFilter
{
    // Half-open spans (startLine, startChar, endLine, endChar), all 0-based - matching Roslyn's LinePosition and
    // the index's 1-based line/col after a -1 shift. Per file, sorted by start and non-overlapping (a single lex
    // never nests a comment inside a string or vice-versa), so a point lands in at most one span.
    // Keyed by EXACT path: on a case-sensitive tree (Linux Samba share, WSL) Reg.h and reg.h are different files, and the
    // paths all come from one index, so their spelling is consistent.
    private readonly Dictionary<string, (int sl, int sc, int el, int ec)[]> _cache = new(StringComparer.Ordinal);

    // The symbol this query is about, and where it occurs in each file's CURRENT text (0-based line, col). The hits being
    // classified carry the INDEX's coordinates; if the file changed since, those coordinates may now land inside a
    // comment/string that wasn't there - suppressing a real reference. A position where the token no longer sits is
    // stale: keep the hit (fail open).
    private readonly string? _token;
    private readonly Dictionary<string, HashSet<(int, int)>> _occurrences = new(StringComparer.Ordinal);

    public LexicalSpanFilter() { }

    /// <param name="token">The queried name; enables the stale-coordinate check.</param>
    public LexicalSpanFilter(string token) => _token = string.IsNullOrEmpty(token) ? null : token;

    /// <summary>True if the match at (1-based line, 1-based column) in <paramref name="path"/> falls inside a
    /// comment or string/char literal - i.e. it is NOT a real code reference and the lexical backfill should skip
    /// it. Non-covered languages (no semantic promise about comments) and any file we can't classify return
    /// false, so their behaviour is unchanged.</summary>
    public bool IsInCommentOrString(string path, int line1Based, int column1Based)
    {
        if (!SemanticCoverage.IsCommentAware(path)) return false;
        var spans = SpansFor(path);
        if (spans.Length == 0) return false;

        int l = line1Based - 1, c = column1Based - 1;
        if (_token is not null && (!_occurrences.TryGetValue(path, out var occ) || !occ.Contains((l, c))))
            return false; // the index's position no longer holds the token (file changed): don't trust the classification
        // Binary-search the last span whose start <= (l,c); since spans are disjoint and sorted, only it can
        // contain the point.
        int lo = 0, hi = spans.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var s = spans[mid];
            if (s.sl < l || (s.sl == l && s.sc <= c)) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return false;
        var sp = spans[found];
        return l < sp.el || (l == sp.el && c < sp.ec);   // (l,c) strictly before the half-open end
    }

    private (int sl, int sc, int el, int ec)[] SpansFor(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        (int, int, int, int)[] spans;
        try
        {
            string text = File.ReadAllText(path);
            spans = SemanticCoverage.IsCSharp(path) ? CSharpSpans(text) : CFamilySpans(text);
            if (_token is not null) _occurrences[path] = Occurrences(text, _token);
        }
        catch
        {
            spans = System.Array.Empty<(int, int, int, int)>();   // unreadable/unparsable -> suppress nothing
        }
        _cache[path] = spans;
        return spans;
    }

    private static HashSet<(int, int)> Occurrences(string text, string token)
    {
        var set = new HashSet<(int, int)>();
        int line = 0, lineStart = 0, scanned = 0, idx;
        while ((idx = text.IndexOf(token, scanned, StringComparison.Ordinal)) >= 0)
        {
            for (int k = scanned; k < idx; k++) if (text[k] == '\n') { line++; lineStart = k + 1; }
            set.Add((line, idx - lineStart));
            scanned = idx + 1;
        }
        return set;
    }

    // --- C#: exact, via Roslyn's lexer -------------------------------------------------------------------------

    private static (int, int, int, int)[] CSharpSpans(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var src = tree.GetText();
        var root = tree.GetRoot();
        var list = new List<(int, int, int, int)>();

        foreach (var tr in root.DescendantTrivia())
        {
            switch (tr.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:   // /// ... (covers <see cref="X"/>)
                case SyntaxKind.MultiLineDocumentationCommentTrivia:    // /** ... */
                    Add(list, src, tr.Span);
                    break;
            }
        }
        foreach (var tok in root.DescendantTokens())
        {
            switch (tok.Kind())
            {
                case SyntaxKind.StringLiteralToken:
                case SyntaxKind.CharacterLiteralToken:
                case SyntaxKind.InterpolatedStringTextToken:            // text parts of $"..."; the {holes} stay code
                case SyntaxKind.Utf8StringLiteralToken:
                case SyntaxKind.SingleLineRawStringLiteralToken:
                case SyntaxKind.MultiLineRawStringLiteralToken:
                case SyntaxKind.Utf8SingleLineRawStringLiteralToken:
                case SyntaxKind.Utf8MultiLineRawStringLiteralToken:
                    Add(list, src, tok.Span);
                    break;
            }
        }

        list.Sort(static (a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        return list.ToArray();
    }

    private static void Add(List<(int, int, int, int)> list, SourceText src, TextSpan span)
    {
        if (span.IsEmpty) return;
        var ls = src.Lines.GetLinePositionSpan(span);
        list.Add((ls.Start.Line, ls.Start.Character, ls.End.Line, ls.End.Character));
    }

    // --- C/C++: conservative single-pass scanner ---------------------------------------------------------------
    // Models //-comments, /* */-comments, "..."/'...' literals (with \\ escapes and \\-newline continuation), and
    // C++ raw strings R"delim( ... )delim". On anything it doesn't recognise it stays in code - never over-reaching
    // into a region that might hold a real reference. A string/char literal that hits end-of-line without closing
    // is treated as ending there (an unterminated literal is almost always a mis-scan; stopping keeps later code
    // on that line eligible).
    private static (int, int, int, int)[] CFamilySpans(string text)
    {
        var list = new List<(int, int, int, int)>();
        int n = text.Length, i = 0, line = 0, col = 0;

        while (i < n)
        {
            char c = text[i];
            char d = i + 1 < n ? text[i + 1] : '\0';

            if (c == '/' && d == '/')
            {
                int sl = line, sc = col;
                // Ends at LF or CR: a CR-only (classic Mac) file has no LF at all, and the comment must not swallow it.
                while (i < n && text[i] != '\n' && text[i] != '\r') { i++; col++; }
                list.Add((sl, sc, line, col));
                continue;
            }
            if (c == '/' && d == '*')
            {
                int sl = line, sc = col;
                i += 2; col += 2;
                while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/'))
                {
                    if (text[i] == '\n') { line++; col = 0; } else col++;
                    i++;
                }
                if (i < n) { i += 2; col += 2; }   // consume the closing */
                list.Add((sl, sc, line, col));
                continue;
            }
            if (c == '"')
            {
                if (IsRawStringOpen(text, i)) { ScanRawString(text, list, ref i, ref line, ref col); continue; }
                int sl = line, sc = col, j = i + 1, jl = line, jc = col + 1;
                bool closed = false;
                while (j < n)
                {
                    char t = text[j];
                    if (t == '\\' && j + 1 < n)
                    {
                        if (text[j + 1] == '\n') { j += 2; jl++; jc = 0; continue; }                     // continuation (LF)
                        if (text[j + 1] == '\r' && j + 2 < n && text[j + 2] == '\n') { j += 3; jl++; jc = 0; continue; } // (CRLF)
                        j += 2; jc += 2; continue;                                                        // escaped char
                    }
                    if (t == '"') { closed = true; j++; jc++; break; }
                    if (t == '\n' || t == '\r') break;                                                    // unterminated at EOL
                    j++; jc++;
                }
                // Fail OPEN, like the char-literal rule: an unterminated string (a stray quote in a macro, a malformed line)
                // is not a string - treating it as one would hide real code to the end of the line.
                if (closed) { list.Add((sl, sc, jl, jc)); i = j; line = jl; col = jc; }
                else { i++; col++; }
                continue;
            }
            if (c == '\'')
            {
                // A digit separator (10'000, 0x1'0000) or an apostrophe abutting an identifier is NOT a char
                // literal; treating it as one would open a bogus literal that swallows real code to end-of-line and
                // DROP a genuine reference. So a '\'' only opens a char literal when preceded by a non-identifier
                // char, AND it must close within a short bound on the same line (char literals are tiny) - anything
                // longer is a stray apostrophe, and we fail OPEN to code rather than risk suppressing a reference.
                // ...except an encoding prefix (L'x', u'x', U'x', u8'x'), which is part of the literal, not an identifier.
                if (i > 0 && IsIdentifierChar(text[i - 1]) && !HasEncodingPrefix(text, i)) { i++; col++; continue; }
                int sl = line, sc = col, j = i + 1, jcol = col + 1, scanned = 0;
                bool closed = false;
                while (j < n && text[j] != '\n' && text[j] != '\r' && scanned < 16)
                {
                    if (text[j] == '\\' && j + 1 < n) { j += 2; jcol += 2; scanned += 2; continue; }
                    if (text[j] == '\'') { closed = true; j++; jcol++; break; }
                    j++; jcol++; scanned++;
                }
                if (closed) { list.Add((sl, sc, line, jcol)); i = j; col = jcol; }   // literals hold no newline
                else { i++; col++; }                                                 // not a literal -> ' is code
                continue;
            }
            if (c == '\n') { line++; col = 0; i++; continue; }
            i++; col++;
        }
        return list.ToArray();   // produced left-to-right, so already sorted
    }

    // A '"' is a raw-string opener only when the preceding 'R' is a standalone string prefix - i.e. the chars
    // immediately before R form a valid (possibly empty) encoding prefix (u8/u/U/L), NOT the tail of an ordinary
    // identifier. Without this guard, FOOR"..." is misread as a raw string whose fake delimiter never closes and
    // swallows real code to EOL - dropping a reference. Fail closed to "not raw" on anything unexpected.
    private static bool IsRawStringOpen(string text, int quote)
    {
        int p = quote - 1;
        if (p < 0 || text[p] != 'R') return false;
        int q = p - 1;
        while (q >= 0 && IsIdentifierChar(text[q])) q--;          // identifier run immediately before R
        string prefix = text.Substring(q + 1, p - (q + 1));
        return prefix.Length == 0 || prefix == "u8" || prefix == "u" || prefix == "U" || prefix == "L";
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Is the identifier run immediately before the quote at `quote` exactly a character-literal encoding prefix?
    private static bool HasEncodingPrefix(string text, int quote)
    {
        int q = quote - 1;
        while (q >= 0 && IsIdentifierChar(text[q])) q--;
        var prefix = text.Substring(q + 1, quote - (q + 1));
        return prefix is "L" or "u" or "U" or "u8";
    }

    private static void ScanRawString(string text, List<(int, int, int, int)> list, ref int i, ref int line, ref int col)
    {
        int n = text.Length, sl = line, sc = col;
        i++; col++;                                      // past the opening quote
        // delimiter = chars up to '(' (C++ allows up to 16, none of ')', '\\', whitespace)
        int delimStart = i;
        while (i < n && text[i] != '(' && text[i] != '\n' && (i - delimStart) <= 16) { i++; col++; }
        if (i >= n || text[i] != '(')
        {
            // Malformed - bail, recording what we consumed as a span so we don't re-enter here.
            list.Add((sl, sc, line, col));
            return;
        }
        string delim = text.Substring(delimStart, i - delimStart);
        i++; col++;                                      // past '('
        string close = ")" + delim + "\"";
        while (i < n)
        {
            if (text[i] == ')' && i + close.Length <= n && string.CompareOrdinal(text, i, close, 0, close.Length) == 0)
            {
                for (int k = 0; k < close.Length; k++) { col++; i++; }   // raw-string body has no newlines in the delimiter
                break;
            }
            if (text[i] == '\n') { line++; col = 0; } else col++;
            i++;
        }
        list.Add((sl, sc, line, col));
    }
}
