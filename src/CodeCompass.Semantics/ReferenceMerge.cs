using CodeCompass.Core.Text;

namespace CodeCompass.Semantics;

/// <summary>
/// The shared DECISIONS of find_references, used by BOTH the CLI (<c>CmdRefs</c>) and the MCP tool handler so
/// the two can't drift. They drifted before: the b5 lexical-fallback fix and the unresolved-include case each
/// reached one path releases before the other, because this logic was copy-pasted. Host-specific concerns -
/// how candidates/analyzers are sourced (warm server + subprocess vs fresh in-process), how paths are
/// displayed (multi-root DisplayPath vs RelativePath), how results are emitted (tool text vs stdout), and the
/// scan cap - stay in each caller; only the decisions live here.
/// </summary>
public static class ReferenceMerge
{
    /// <summary>A trigram hit qualifies as a LEXICAL reference to a symbol of length <paramref name="nameLength"/>
    /// when: it is NOT in a semantically-covered file (unless the C/C++ pass was incomplete - then cover those
    /// too, so a symbol whose semantic resolution failed isn't dropped to a bare zero), it is a code file (not
    /// build noise like .lst/.bak/.o), and it is a whole-word match (not a substring). Callers dedup by their
    /// own display key and emit.</summary>
    public static bool IsLexicalReference(string path, string lineText, int column1Based, int nameLength, bool cppIncomplete)
        => !(SemanticCoverage.IsCovered(path) && !cppIncomplete)
           && ReferenceFileFilter.IsCodeReference(path)
           && WordBoundary.IsWholeWord(lineText, column1Based - 1, nameLength);

    /// <summary>The honest C/C++ coverage caveats for a query as a list of bit strings (empty if fully
    /// covered): how many candidate TUs parsed, whether the memory budget stopped it, and which #includes were
    /// unresolved. Each host wraps these bits in its own surface prose (CLI stderr line vs MCP note); sharing
    /// the bits keeps the substance (wording, thresholds, header list) identical across both.</summary>
    public static List<string> CppCoverageBits(int cppParsed, int cppCandidates, bool memoryStopped, IReadOnlyList<string> unresolvedIncludes)
    {
        var bits = new List<string>();
        if (cppParsed < cppCandidates) bits.Add($"{cppParsed:N0}/{cppCandidates:N0} candidate C/C++ file(s) parsed");
        if (memoryStopped) bits.Add("semantic pass hit its memory budget and stopped early (remaining C/C++ refs shown lexically; raise CODECOMPASS_CPP_SESSION_MEM_MB for the per-session ceiling or CODECOMPASS_CPP_QUERY_MEM_MB for a single query)");
        if (unresolvedIncludes.Count > 0)
        {
            var shown = string.Join(", ", unresolvedIncludes.Take(5));
            if (unresolvedIncludes.Count > 5) shown += $", +{unresolvedIncludes.Count - 5} more";
            bits.Add($"{unresolvedIncludes.Count} unresolved #include(s): {shown}");
        }
        return bits;
    }
}
