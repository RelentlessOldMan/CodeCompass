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
    /// <summary>Max lexical hits any ONE file may contribute to the reference backfill. A common macro-like
    /// name can appear hundreds of times in a single giant build-log / disassembly-echo file; without a
    /// per-file bound those crowd out the whole result cap and starve the real references in ordinary source
    /// files (the UNC refs-gap). Deliberately a small fraction of typical result budgets (MCP find_references
    /// defaults to 100, the CLI backfill to 1000): with a handful of noisy files each bounded to this, the
    /// budget still reaches the real references. Also generous enough that a normal source file - which rarely
    /// holds more than a handful of references to one symbol - is never truncated; it only reins in
    /// pathological high-hit files. Passed to <c>SegmentedIndex.Search(..., maxPerFile:)</c> by both paths.</summary>
    public const int MaxLexicalHitsPerFile = 16;

    /// <summary>A trigram hit qualifies as a LEXICAL reference to a symbol of length <paramref name="nameLength"/>
    /// when: it is NOT in a semantically-covered file whose language pass was COMPLETE, it is a code file (not
    /// build noise like .lst/.bak/.o), and it is a whole-word match (not a substring). Incompleteness is
    /// resolved PER LANGUAGE: a .cs file backfills when the C# pass was incomplete (<paramref
    /// name="csharpIncomplete"/> - e.g. #if-guarded code Roslyn couldn't see), a .c/.cpp/.h when the C/C++
    /// pass was (<paramref name="cppIncomplete"/> - memory-stop / unparsed TU / unresolved include). This is
    /// what stops a symbol whose semantic resolution silently missed part of the tree from being reported as a
    /// bare zero, without over-firing lexical on the OTHER language that resolved cleanly. Callers dedup by
    /// their own display key and emit.</summary>
    public static bool IsLexicalReference(string path, string lineText, int column1Based, int nameLength, bool cppIncomplete, bool csharpIncomplete)
    {
        bool languageIncomplete = SemanticCoverage.IsCSharp(path) ? csharpIncomplete : cppIncomplete;
        return !(SemanticCoverage.IsCovered(path) && !languageIncomplete)
           && ReferenceFileFilter.IsCodeReference(path)
           && WordBoundary.IsWholeWord(lineText, column1Based - 1, nameLength);
    }

    /// <summary>The honest C/C++ coverage caveats for a query as a list of bit strings (empty if fully
    /// covered): how many candidate TUs parsed, whether the memory budget stopped it, and which #includes were
    /// unresolved. Each host wraps these bits in its own surface prose (CLI stderr line vs MCP note); sharing
    /// the bits keeps the substance (wording, thresholds, header list) identical across both.</summary>
    public static List<string> CppCoverageBits(int cppParsed, int cppCandidates, bool memoryStopped, IReadOnlyList<string> unresolvedIncludes, bool tooManyCandidates = false)
    {
        var bits = new List<string>();
        // A deliberate broad-symbol short-circuit: the semantic pass was skipped UP FRONT because the candidate
        // set exceeded the limit (parsing it would grind for minutes and fall back to lexical anyway). Say so
        // distinctly - and DON'T also emit the generic "0/N parsed"/memory-stop lines, which would misread the
        // intentional skip as a failure. Name the knob so a caller who wants precision can override it.
        if (tooManyCandidates)
        {
            bits.Add($"{cppCandidates:N0} candidate C/C++ file(s) exceeded the semantic-parse limit, so references are shown lexically (raise CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES, or set it to 0, to force a semantic parse)");
            return bits;
        }
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
