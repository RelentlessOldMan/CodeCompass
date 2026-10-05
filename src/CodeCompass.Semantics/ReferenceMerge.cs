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
    public static bool IsLexicalReference(string path, string lineText, int column1Based, int nameLength, bool cppIncomplete, bool csharpIncomplete,
        LexicalSpanFilter? spanFilter = null, int line1Based = 0, int lineTextOffset = 0)
    {
        bool languageIncomplete = SemanticCoverage.IsCSharp(path) ? csharpIncomplete : cppIncomplete;
        return !(SemanticCoverage.IsCovered(path) && !languageIncomplete)
           && ReferenceFileFilter.IsCodeReference(path)
           // lineText may be a window of a very long line (SearchMatch.LineTextOffset): index the match within it.
           && WordBoundary.IsWholeWord(lineText, column1Based - 1 - lineTextOffset, nameLength)
           // On the covered languages (where this fires only because the semantic pass was incomplete) a whole-word
           // hit inside a comment or string is NOT a reference - the semantic pass excludes exactly those, and the
           // tool's description promises the same. Skip them so the backfill recovers #if-guarded USES without
           // re-admitting <see cref> doc-comment / string-literal noise. No filter (or a position we can't map) ->
           // keep the hit, never drop a real reference.
           && (spanFilter is null || line1Based <= 0 || !spanFilter.IsInCommentOrString(path, line1Based, column1Based));
    }

    /// <summary>The honest C# conditional-compilation caveat for a query, or empty when none applies. Roslyn
    /// builds its model with an EMPTY preprocessor set, so references in inactive <c>#if</c>/<c>#elif</c> branches
    /// are invisible to the semantic pass (shown lexically where the backfill finds them). Unlike the old generic
    /// wording, this NAMES the candidate file(s) that actually use conditional compilation - a user who greps the
    /// result files and finds no <c>#if</c> can see where it really is (the trip file is often a candidate that
    /// isn't itself a result). Each host wraps the returned bare clause in its own surface prose.</summary>
    public static string CSharpConditionalNote(IReadOnlyList<string> conditionalFilesDisplay)
    {
        if (conditionalFilesDisplay.Count == 0) return "";
        return "C# coverage INCOMPLETE - conditional compilation (#if/#elif) in " + FileList(conditionalFilesDisplay) +
               " hides inactive-branch references from the semantic pass (shown lexically where found), so a low count may miss #if-guarded uses.";
    }

    /// <summary>The find_callees twin of <see cref="CSharpConditionalNote"/>: names the conditional file(s) and
    /// says what happened to the inactive-branch calls - recovered by name below (<paramref name="recoveredCount"/>
    /// &gt; 0) or, if none resolved, simply possibly-missing. Shared so the CLI and MCP disclose identically.</summary>
    public static string CSharpConditionalCalleesNote(IReadOnlyList<string> conditionalFilesDisplay, int recoveredCount)
    {
        if (conditionalFilesDisplay.Count == 0) return "";
        string tail = recoveredCount > 0
            ? "; calls guarded by it are recovered by name below (may include unrelated same-named declarations)"
            : "; calls guarded by it aren't seen by the semantic pass, so some may be missing";
        return "C# coverage INCOMPLETE - conditional compilation (#if/#elif) in " + FileList(conditionalFilesDisplay) + tail + ".";
    }

    /// <summary>Header for the segregated section of callees recovered from inactive <c>#if</c>/<c>#elif</c>
    /// branches - resolved by NAME (disabled text can't be semantically bound, so the repo-wide name match can
    /// surface unrelated same-named methods/types), kept separate from the authoritative semantic list and
    /// flagged for verification.</summary>
    public static string CSharpInactiveCalleesHeader(int count) =>
        $"-- {count} more callee(s) in #if/#elif-guarded branches, resolved by NAME (may include unrelated same-named declarations; verify with find_definition):";

    private static string FileList(IReadOnlyList<string> files)
    {
        var shown = string.Join(", ", files.Take(5));
        if (files.Count > 5) shown += $", +{files.Count - 5} more";
        return shown;
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
