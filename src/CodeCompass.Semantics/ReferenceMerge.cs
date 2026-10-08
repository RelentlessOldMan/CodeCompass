using CodeCompass.Core.Text;

namespace CodeCompass.Semantics;

/// <summary>
/// The shared DECISIONS of find_references, used by BOTH the CLI (<c>CmdRefs</c>) and the MCP tool handler so
/// the two can't drift (they did, back when this logic was copy-pasted). Host-specific concerns - how paths are
/// displayed (multi-root DisplayPath vs RelativePath), how results are emitted (tool text vs stdout), and the
/// scan cap - stay in each caller; only the decisions live here.
///
/// <para>Two kinds of answer: C# is SEMANTIC (Roslyn), with a name-match backfill only where Roslyn can't see
/// (#if-guarded code, unreadable files). Everything else is matched by NAME from the text index - whole word, in a
/// code file, and for C and C++ never inside a comment or string literal.</para>
/// </summary>
public static class ReferenceMerge
{
    /// <summary>Max name matches any ONE file may contribute. Reference searches only consider code files (see
    /// <see cref="ReferenceFileFilter.IsCodeReference"/>, applied before the scan), so this no longer has to fend off
    /// build logs; it keeps one huge generated file (a register map naming a symbol thousands of times) from filling
    /// the whole answer, while leaving ordinary files - which hold a handful to a few dozen uses - untruncated. When
    /// it does cut a file, the answer says so. Passed to <c>SegmentedIndex.Search(..., maxPerFile:)</c> by both paths.</summary>
    public const int MaxLexicalHitsPerFile = 64;

    /// <summary>Dedup keys (<c>display:line:col</c>) for the DEFINITIONS of the queried name, from the symbol index, in
    /// every language it covers. Callers seed their already-seen set with these so a name match never lists where a
    /// symbol is defined as a use of it (Roslyn never reports a declaration as a reference either - field report v4).
    /// Exact name-token position, so a real use that merely shares the definition's line is still counted. This relies
    /// on the symbol queries capturing definitions ONLY (see LanguageRegistry) - a captured use would be dropped.</summary>
    public static IEnumerable<string> DefinitionKeys(IEnumerable<CodeCompass.Core.Symbols.Symbol> definitions, Func<string, string> display) =>
        DefinitionKeys(definitions, display, CFamilyDefinitionsOnlySince);

    /// <summary>The index content version from which C/C++ symbols are definitions only (BuildInfo history, v4).</summary>
    public const int CFamilyDefinitionsOnlySince = 4;

    /// <summary><see cref="DefinitionKeys(IEnumerable{CodeCompass.Core.Symbols.Symbol}, Func{string, string})"/> for an
    /// index built at <paramref name="indexContentVersion"/>. An index from before C/C++ symbols became definitions-only
    /// also recorded type uses (<c>struct node *n</c>) and macro calls (<c>list_for_each(...) {</c>) as symbols, so its
    /// C/C++ positions are left out: the definition may then be listed as a use, but a real use is never dropped.</summary>
    public static IEnumerable<string> DefinitionKeys(IEnumerable<CodeCompass.Core.Symbols.Symbol> definitions, Func<string, string> display,
                                                     int indexContentVersion) =>
        definitions.Where(s => indexContentVersion >= CFamilyDefinitionsOnlySince || !SemanticCoverage.IsCFamily(s.RelativePath))
                   .Select(s => $"{display(s.RelativePath)}:{s.Line}:{s.Column}");

    /// <summary>Which files a reference name search reads: code only (never logs/docs/data), and .cs only when the C#
    /// semantic pass couldn't see everything - otherwise Roslyn already answered for C# and those files would only use up
    /// the scan budget.</summary>
    public static Func<string, bool> ReferencePathFilter(bool csharpIncomplete) =>
        p => ReferenceFileFilter.IsCodeReference(p) && (csharpIncomplete || !SemanticCoverage.IsCSharp(p));

    /// <summary><see cref="ReferencePathFilter(bool)"/>, plus the .cs files in <paramref name="csBackfill"/> (absolute paths)
    /// even when the C# pass is otherwise complete: files it couldn't fully see on their own (see
    /// RoslynCSharpAnalyzer.UsingsClash). Paths the search passes are relative to <paramref name="root"/>.</summary>
    public static Func<string, bool> ReferencePathFilter(bool csharpIncomplete, string root, ISet<string>? csBackfill)
    {
        if (csBackfill is null || csBackfill.Count == 0) return ReferencePathFilter(csharpIncomplete);
        return p => ReferenceFileFilter.IsCodeReference(p) &&
                    (csharpIncomplete || !SemanticCoverage.IsCSharp(p) || csBackfill.Contains(FullPath(root, p)));
    }

    private static string FullPath(string root, string rel) => Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The per-match test a reference name search applies DURING the scan, so only real references count toward
    /// its budget (see SegmentedIndex.Search's <c>accept</c>): whole word, not in a comment/string, and not already listed
    /// (a semantic hit or a definition, keyed by <paramref name="displayKey"/>:line:col). Shared by the CLI and MCP.</summary>
    public static Func<CodeCompass.Core.Indexing.SearchMatch, bool> ReferenceAccept(string root, int nameLength, bool csharpIncomplete,
        LexicalSpanFilter spanFilter, ISet<string> alreadyListed, Func<string, string> displayKey, ISet<string>? csBackfill = null)
    {
        // Runs on the caller's thread, a file's hits at a time (SegmentedIndex.TakeAccepted), so the per-file path and
        // display prefix are computed once per file, not once per raw hit.
        string? lastRel = null, lastFull = null, lastDisplay = null;
        return m =>
        {
            // Cheapest first: most raw hits of a broad name are inside longer identifiers (GetFoo for Get), rejected here
            // with no allocation. Then already-listed (a set lookup), and only then IsLexicalReference, which may read and
            // lex the whole file - for a name defined in thousands of generated files, over a second only to drop definitions.
            if (!WordBoundary.IsWholeWord(m.LineText, m.Column - 1 - m.LineTextOffset, nameLength)) return false;
            if (!string.Equals(m.Path, lastRel, StringComparison.Ordinal))
            {
                lastRel = m.Path;
                lastFull = FullPath(root, m.Path);
                lastDisplay = displayKey(m.Path);
            }
            if (alreadyListed.Contains($"{lastDisplay}:{m.Line}:{m.Column}")) return false;
            return IsLexicalReference(lastFull!, m.LineText, m.Column, nameLength, csharpIncomplete, spanFilter, m.Line, m.LineTextOffset,
                                      csBackfill);
        };
    }

    /// <summary>The name search's text hand-off (SegmentedIndex.Search's <c>textSink</c>): each candidate file it read whole
    /// is offered to <paramref name="spanFilter"/>, so classifying that file's hits doesn't read it again; null withdraws it.</summary>
    public static Action<string, string?> ReferenceTextSink(string root, LexicalSpanFilter spanFilter) =>
        (rel, text) => spanFilter.Offer(Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))), text);

    /// <summary>Files the name search could not read just now (locked by an editor or another program, an ACL, a network
    /// error): references in them are missing, so the answer must not read as complete. Empty when none.</summary>
    public static string UnreadableCandidatesNote(IReadOnlyCollection<string> displayPaths) =>
        displayPaths.Count == 0 ? "" :
        $"{displayPaths.Count} file(s) could not be read just now, so any references in them are NOT listed: " +
        FileList(displayPaths.OrderBy(p => p, StringComparer.Ordinal).ToList()) +
        " (another program may have them open; rerun the query)";

    /// <summary>A qualified C++ name (<c>Widget::spin</c>, <c>ns::f</c>) is searched by its last part: uses like
    /// <c>w.spin(3)</c> never spell the qualifier, so matching the whole string finds only the definition. Returns the
    /// name to search and, when it was changed, a note saying so (null otherwise).</summary>
    public static (string Name, string? Note) MemberOfQualified(string name)
    {
        // Template arguments are skipped when looking for the last "::" (std::vector<std::string> is `vector`, not
        // `string>`) and dropped from the result: a name search needs a plain identifier.
        int depth = 0, lastSep = -1, firstOpen = -1;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            // An operator name (operator<<, operator<=>) runs to the end: its '<'s are part of the name, not brackets.
            if (depth == 0 && IsOperatorKeyword(name, i)) { firstOpen = -1; break; }
            if (c == '<') { if (depth++ == 0 && firstOpen < 0) firstOpen = i; }
            else if (c == '>') { if (depth > 0) depth--; }
            else if (depth == 0 && c == ':' && i + 1 < name.Length && name[i + 1] == ':') { lastSep = i; firstOpen = -1; i++; }
        }
        int start = lastSep < 0 ? 0 : lastSep + 2;
        int end = firstOpen >= start ? firstOpen : name.Length;
        var member = name[start..end].Trim();
        if (member.Length == 0 || member == name) return (name, null);
        return (member, $"\"{name}\" was matched by the name \"{member}\" (uses like obj.{member}(...) don't spell the " +
                        $"qualifier or template arguments), so other symbols named {member} are listed too");
    }

    private static bool IsOperatorKeyword(string s, int i)
    {
        const string kw = "operator";
        if (string.CompareOrdinal(s, i, kw, 0, kw.Length) != 0) return false;
        bool startOk = i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_');
        int after = i + kw.Length;
        bool endOk = after >= s.Length || !(char.IsLetterOrDigit(s[after]) || s[after] == '_');
        return startOk && endOk;
    }

    /// <summary>What C/C++ reference lines are, said once per answer that has any.</summary>
    public const string CppByNameNote =
        "C/C++ references are matched by NAME in code (comments and strings excluded; C/C++ is not compiled), " +
        "so uses of different symbols that share this name are listed together";

    /// <summary>A text-index hit counts as a reference to a symbol of length <paramref name="nameLength"/> when: it is
    /// NOT in a .cs file whose semantic pass was complete (that file's references came from Roslyn), it is a code file
    /// (not build noise like .lst/.bak/.o or data like .json/.csv), it is a whole-word match, and - for C# and C/C++ -
    /// it is not inside a comment or string literal. <paramref name="csharpIncomplete"/>: the C# pass couldn't see
    /// everything (#if-guarded code, unreadable files), so .cs name matches backfill it. Callers dedup by their own
    /// display key and emit.</summary>
    public static bool IsLexicalReference(string path, string lineText, int column1Based, int nameLength, bool csharpIncomplete,
        LexicalSpanFilter? spanFilter = null, int line1Based = 0, int lineTextOffset = 0, ISet<string>? csBackfill = null)
    {
        return !(SemanticCoverage.IsCSharp(path) && !csharpIncomplete && csBackfill?.Contains(path) != true)
           && ReferenceFileFilter.IsCodeReference(path)
           // lineText may be a window of a very long line (SearchMatch.LineTextOffset): index the match within it.
           && WordBoundary.IsWholeWord(lineText, column1Based - 1 - lineTextOffset, nameLength)
           // A whole-word hit inside a comment or string is NOT a reference (an <see cref> doc comment, a "name" in a
           // log message). No filter (or a position we can't map) -> keep the hit, never drop a real reference.
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

    /// <summary>The C# twin of an unparsed TU: sources the semantic model couldn't READ (an editor's exclusive lock,
    /// an access error) are absent from it, so references in them come only from the lexical backfill.</summary>
    public static string CSharpUnreadableNote(IReadOnlyList<string> unreadableFilesDisplay) =>
        unreadableFilesDisplay.Count == 0 ? "" :
        "C# coverage INCOMPLETE - " + FileList(unreadableFilesDisplay) + " could not be read when the semantic model was built, " +
        "so references in them are shown lexically (they'll be picked up after the next edit/reindex).";

    /// <summary>The C# global-usings clash caveat (see RoslynCSharpAnalyzer.UsingsClash), or empty when there is none.
    /// <paramref name="declarationClash"/>: a clash in a declaration, whose effect reaches other files - the whole C# pass is
    /// backfilled by name. Otherwise only <paramref name="fileClash"/> (candidate files with a clash) are.</summary>
    public static string CSharpUsingsClashNote(IReadOnlyList<string> declarationClash, IReadOnlyList<string> fileClash)
    {
        if (declarationClash.Count == 0 && fileClash.Count == 0) return "";
        var files = declarationClash.Concat(fileClash).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return "C# coverage INCOMPLETE - " + FileList(files) + " belong" + (files.Count == 1 ? "s" : "") + " to a project without " +
               "ImplicitUsings, and the SDK projects' global usings make a name there ambiguous, so references through it are " +
               (declarationClash.Count > 0 ? "shown by name across the C# files" : "shown by name in " + (files.Count == 1 ? "that file" : "those files")) + ".";
    }

    private static string FileList(IReadOnlyList<string> files)
    {
        var shown = string.Join(", ", files.Take(5));
        if (files.Count > 5) shown += $", +{files.Count - 5} more";
        return shown;
    }
}
