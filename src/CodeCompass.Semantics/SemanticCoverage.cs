namespace CodeCompass.Semantics;

/// <summary>Which files get which kind of reference matching. C# has a semantic analyzer (Roslyn). C and C++ are matched
/// by NAME, but comment- and string-aware, like C#'s backfill.</summary>
public static class SemanticCoverage
{
    private static readonly HashSet<string> CFamilyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".c++",
        ".h", ".hpp", ".hh", ".hxx", ".h++", ".inl", ".ipp", ".tcc",
    };

    /// <summary>Is this a C# source file (the one language with a semantic reference pass)?</summary>
    public static bool IsCSharp(string path) => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Is this a C or C++ source/header file? Its references are matched by name.</summary>
    public static bool IsCFamily(string path) => CFamilyExtensions.Contains(Path.GetExtension(path));

    /// <summary>Languages whose name matches skip comments and string literals (we can lex them reliably).</summary>
    public static bool IsCommentAware(string path) => IsCSharp(path) || IsCFamily(path);

    /// <summary>True if C# source uses conditional compilation (<c>#if</c>/<c>#elif</c>/<c>#else</c>). Roslyn
    /// builds its model with an EMPTY preprocessor-symbol set, so code in inactive branches is parsed as
    /// disabled text - invisible to semantic find_references AND find_callees. A candidate file with these
    /// directives means the semantic pass may have SILENTLY missed guarded references/calls, so callers
    /// disclose it (and refs backfills lexically). Cheap allocation-free scan of line starts.</summary>
    public static bool HasCSharpConditionalCompilation(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int i = 0, n = text.Length;
        while (i < n)
        {
            int j = i;
            while (j < n && (text[j] == ' ' || text[j] == '\t')) j++;   // leading whitespace
            if (j < n && text[j] == '#')
            {
                j++;
                while (j < n && (text[j] == ' ' || text[j] == '\t')) j++; // '#' and optional space, e.g. "# if"
                if (Kw(text, j, "if") || Kw(text, j, "elif") || Kw(text, j, "else")) return true;
            }
            while (i < n && text[i] != '\n') i++; // to end of line
            i++;                                   // past '\n'
        }
        return false;

        static bool Kw(string s, int at, string kw)
        {
            if (at + kw.Length > s.Length) return false;
            for (int k = 0; k < kw.Length; k++) if (s[at + k] != kw[k]) return false;
            int after = at + kw.Length;                       // word boundary so "ifdef"/"elsewhere" don't match
            return after >= s.Length || !char.IsLetterOrDigit(s[after]);
        }
    }

    /// <summary>The candidate <c>.cs</c> files that use conditional compilation - the SINGLE source of truth for "the C# semantic pass may be
    /// incomplete, so backfill lexical + disclose rather than trust a possibly-partial result." Roslyn can't see
    /// inactive <c>#if</c>/<c>#elif</c> branches, so any such candidate means a guarded reference/call may be
    /// missing; an empty list means the pass was complete (no backfill, no disclosure). The disclosure NAMES these
    /// so a user who greps the result files and finds no <c>#if</c> can see which candidate really carries it (a
    /// trip file is often a trigram candidate that isn't itself a result). Full paths in candidate order; callers
    /// take <c>.Count &gt; 0</c> for the incomplete flag and map the paths to a display form. Reads each candidate
    /// (bounded to those that could contain the symbol); unreadable files are skipped.</summary>
    public static IReadOnlyList<string> CSharpConditionalFiles(IEnumerable<string> candidateFullPaths)
    {
        var hits = new List<string>();
        foreach (var p in candidateFullPaths)
        {
            if (!IsCSharp(p)) continue;
            try { if (HasCSharpConditionalCompilation(File.ReadAllText(p))) hits.Add(p); }
            catch { /* unreadable candidate: can't prove incompleteness from it */ }
        }
        return hits;
    }
}
