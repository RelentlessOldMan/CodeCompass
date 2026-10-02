namespace CodeCompass.Semantics;

/// <summary>Which file types have a semantic analyzer (so lexical fallback can skip them).</summary>
public static class SemanticCoverage
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",                                   // Roslyn
        ".c", ".cc", ".cpp", ".cxx", ".c++",     // clang (C/C++)
        ".h", ".hpp", ".hh", ".hxx",
    };

    public static bool IsCovered(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>Is this a C# source file (the Roslyn-covered language, distinct from the clang-covered C/C++
    /// set)? Lets the lexical backfill decide incompleteness PER LANGUAGE - a .cs file backfills when the C#
    /// pass was incomplete, a .c/.cpp/.h when the C/C++ pass was.</summary>
    public static bool IsCSharp(string path) => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Whether the C# semantic pass may be INCOMPLETE for this query: true if ANY candidate .cs file
    /// uses conditional compilation, since Roslyn can't see inactive <c>#if</c> branches. Early-exits on the
    /// first hit. Reads candidate files (bounded to those that could contain the symbol); unreadable files are
    /// skipped. Mirrors <see cref="IsCppPassIncomplete"/> as the single source of truth for "backfill lexical
    /// + disclose rather than trust a possibly-partial C# semantic result."</summary>
    public static bool IsCSharpPassIncomplete(IEnumerable<string> candidateFullPaths)
    {
        foreach (var p in candidateFullPaths)
        {
            if (!IsCSharp(p)) continue;
            try { if (HasCSharpConditionalCompilation(File.ReadAllText(p))) return true; }
            catch { /* unreadable candidate: can't prove incompleteness from it */ }
        }
        return false;
    }

    /// <summary>The candidate <c>.cs</c> files that actually use conditional compilation - the evidence behind
    /// <see cref="IsCSharpPassIncomplete"/>. The disclosure names these so a user who greps the result files and
    /// finds no <c>#if</c> can see which candidate really carries it (a trip file is often a trigram candidate
    /// that isn't itself a result). Returns full paths in candidate order; callers map to a display form. An
    /// empty list means the C# pass was complete (no backfill, no disclosure).</summary>
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

    /// <summary>
    /// Whether a C/C++ <c>find_references</c> pass was INCOMPLETE, i.e. a low/zero semantic count may mean
    /// "couldn't look," not "no references" - so the lexical layer should backfill C/C++ files instead of
    /// treating them as fully covered. True when the pass stopped for memory, some candidate TUs failed to
    /// parse (<paramref name="parsedTus"/> &lt; <paramref name="candidateTus"/>), OR any <c>#include</c> was
    /// unresolved (a TU can PARSE with errors - parsed==candidate - yet resolve nothing, so parsed==candidate
    /// does NOT prove completeness).
    ///
    /// <para>This is the SINGLE source of truth for that decision. It was previously duplicated in the CLI
    /// (<c>CmdRefs</c>) and the MCP tool handler, which drifted: the b5 lexical-fallback fix reached the MCP
    /// path in 1.0.176 but not the CLI until 1.0.183, and the unresolved-include case was missed on both
    /// until 1.0.187. One predicate both call removes that whole divergence class.</para>
    /// </summary>
    public static bool IsCppPassIncomplete(bool memoryStopped, int parsedTus, int candidateTus, int unresolvedIncludeCount)
        => memoryStopped || parsedTus < candidateTus || unresolvedIncludeCount > 0;
}
