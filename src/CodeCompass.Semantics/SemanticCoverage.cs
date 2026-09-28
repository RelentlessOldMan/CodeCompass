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
