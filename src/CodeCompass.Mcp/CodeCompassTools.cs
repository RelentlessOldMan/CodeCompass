using System.ComponentModel;
using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Text;
using CodeCompass.Semantics;
using ModelContextProtocol.Server;

namespace CodeCompass.Mcp;

/// <summary>
/// The tools exposed to the agent. Each returns compact, ranked file:line:col results so
/// the agent gets exactly the lines it needs instead of reading whole files. If the index
/// isn't ready yet, a tool returns a short status (still indexing, or how to build it) so
/// the agent can relay progress rather than hang. Deliberately a small surface (6 tools) to
/// keep the per-session token cost low.
/// </summary>
[McpServerToolType]
public static class CodeCompassTools
{
    [McpServerTool(Name = "search_code")]
    [Description("Search the indexed codebase for a literal text/substring. Case-sensitive by default; " +
                 "set caseSensitive=false to match any case. Returns ranked 'file:line:col: matched " +
                 "line' results. Prefer this over grep or reading whole files - it is faster and " +
                 "returns only the relevant lines.")]
    public static string SearchCode(
        [Description("Literal substring to find.")] string query,
        [Description("Maximum number of results.")] int maxResults = 50,
        [Description("Whether the match is case-sensitive (default true). Set false to match any case.")] bool caseSensitive = true)
        => ServerContext.QueryAll(handles =>
    {
        // Whitespace-only queries have no trigrams, so they'd fall to a full-corpus scan returning noise at
        // high I/O cost - reject them like the other tools do (IsNullOrWhiteSpace, not IsNullOrEmpty).
        if (string.IsNullOrWhiteSpace(query)) return "Provide a non-empty search string.";
        maxResults = Math.Clamp(maxResults, 1, 1000); // agent-supplied; guard against 0/negative/absurd
        // Federate across the primary index + every linked root. Fetch one extra per index to detect
        // truncation across the union; primary hits stay repo-relative, linked hits show absolute paths.
        var hits = new List<(ServerContext.IndexHandle H, SearchMatch M)>();
        foreach (var h in handles)
        {
            foreach (var m in h.Text.Search(query, maxResults + 1, caseSensitive)) hits.Add((h, m));
            if (hits.Count > maxResults) break; // enough to know the union is truncated
        }
        if (hits.Count == 0)
            return (caseSensitive
                ? $"No matches for \"{query}\". Tip: retry with caseSensitive:false for a case-insensitive match, or try a shorter/more distinctive substring."
                : $"No matches for \"{query}\". Tip: try a shorter or more distinctive substring.") + CoverageCaveat();

        bool truncated = hits.Count > maxResults;
        var sb = new StringBuilder();
        foreach (var (h, m) in hits.Take(maxResults)) sb.AppendLine($"{DisplayPath(h, m.Path)}:{m.Line}:{m.Column}: {m.LineText}");
        sb.Append(Footer(Math.Min(hits.Count, maxResults), truncated, "match", "matches"));
        return sb.ToString();
    });

    // A federated hit's display path: primary (project) hits stay repo-relative for compactness; a hit
    // from a linked external root is shown as its ABSOLUTE path, so it's unambiguous and directly readable.
    private static string DisplayPath(ServerContext.IndexHandle h, string rel) =>
        h.IsPrimary ? rel : System.IO.Path.GetFullPath(System.IO.Path.Combine(h.Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    // Is a semantic hit's owning root currently in this query's scope? The C#/C++ analyzers span ALL roots
    // (so cross-root resolution keeps working), so when a session focus is active we must filter the DISPLAYED
    // semantic hits to the focused set ourselves - mirroring the handle filter QueryAll already applies to the
    // lexical/symbol paths. SemanticLocation.Root is "" for the primary root, else the linked root's path.
    private static bool InScope(IReadOnlyList<ServerContext.IndexHandle> handles, string semRoot)
    {
        // Empty Root => a primary-root hit: in scope iff the primary handle survived the focus filter. Resolve
        // this first so NormPath (Path.GetFullPath, which throws on "") is only ever called on a real path.
        bool primary = string.IsNullOrEmpty(semRoot);
        foreach (var h in handles)
        {
            if (primary) { if (h.IsPrimary) return true; }
            else if (!h.IsPrimary && string.Equals(NormPath(h.Root), NormPath(semRoot), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string NormPath(string p) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p));

    // A semantic hit's display path. The analyzers span all roots; a hit in the primary root carries an
    // empty Root (repo-relative), a hit in a linked root carries that root (shown absolute) - same
    // addressing convention as the lexical/symbol federation above.
    private static string DisplayPath(CodeCompass.Semantics.SemanticLocation s) =>
        string.IsNullOrEmpty(s.Root)
            ? s.RelativePath
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(s.Root, s.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    // Honest zeros: a "nothing found" is only true for what's INDEXED. Files excluded by the size cap
    // aren't searched at all, so a match could be in one - disclose it rather than let the agent read a
    // zero as "doesn't exist." Empty when there are no known coverage gaps. (Coverage is recorded in the
    // per-repo meta at build/update time.)
    private static string CoverageCaveat(bool includeSymbolSkipped = false)
    {
        try
        {
            var meta = CodeCompass.Core.Storage.IndexMetaFile.Read(ServerContext.Root);
            if (meta is null) return "";
            var sb = new StringBuilder();
            if (meta.FilesOverCap > 0)
                sb.Append($" (Note: {meta.FilesOverCap:N0} file(s) exceed the size cap and are NOT indexed - " +
                          "a match could be in one; run `codecompass survey` to see them.)");
            // Symbol-only gap: files that ARE text-searchable but had NO symbols extracted (over the symbol
            // cap, a numeric data blob, or streamed). A go-to-definition zero could be one of these, so the
            // symbol tools disclose it - search_code already covers these files, so it doesn't.
            if (includeSymbolSkipped && meta.FilesSymbolSkipped > 0)
                sb.Append($" (Note: {meta.FilesSymbolSkipped:N0} large/generated file(s) are text-searchable but " +
                          "had NO symbols extracted - a definition could be in one; try search_code, or run " +
                          "`codecompass survey`.)");
            // Actionable staleness, surfaced where the agent sees it (not only in the central log / doctor):
            // the index's OUTPUT logic is behind this binary, so a rebuild would change results. Content-version
            // keyed, so it stays silent across ordinary product-version upgrades (no cry-wolf).
            var behind = CodeCompass.Core.Storage.IndexMetaFile.BehindNote(meta, ServerContext.Root);
            if (behind.Length > 0) sb.Append($" (Note: {behind}.)");
            return sb.ToString();
        }
        catch { /* meta is best-effort; a missing caveat just omits the note */ }
        return "";
    }

    // A whole-word text match in a data/doc file or a build artifact isn't a code reference - shared with
    // the CLI `refs` via CodeCompass.Core.Text.ReferenceFileFilter so both filter identically.
    private static bool IsCodeReferenceFile(string path) => ReferenceFileFilter.IsCodeReference(path);

    // C/C++ translation-unit extensions (headers are parsed via #include, not directly) - the files the
    // clang layer parses, and thus the candidates worth handing it for a targeted find-references.
    private static readonly HashSet<string> CppSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".c", ".cc", ".cpp", ".cxx", ".c++" };

    private static bool IsCppSourceFile(string path) =>
        CppSourceExtensions.Contains(System.IO.Path.GetExtension(path));

    // Result footer that distinguishes an exact count from a truncated one, so the agent knows
    // whether it has seen everything or must refine the query. `shown` is how many we actually list.
    private static string Footer(int shown, bool truncated, string singular, string plural) =>
        truncated
            ? $"(showing the first {shown} {plural}; MORE EXIST - narrow the query, e.g. add surrounding text or a longer/more specific identifier)"
            : $"({shown} {(shown == 1 ? singular : plural)})";

    [McpServerTool(Name = "find_definition")]
    [Description("Find where a symbol (class, method, function, type, etc.) is defined, by exact name. " +
                 "Returns 'file:startLine-endLine: Kind Name'; for a single small definition it also " +
                 "inlines the source so you don't need to open the file. Use this for go-to-definition.")]
    public static string FindDefinition(
        [Description("Exact symbol name (case-sensitive).")] string name)
        => ServerContext.QueryAll(handles =>
    {
        if (string.IsNullOrWhiteSpace(name)) return "Provide a symbol name.";
        // Federate go-to-definition across the primary + linked roots.
        var matches = new List<(ServerContext.IndexHandle H, Symbol S)>();
        foreach (var h in handles)
            foreach (var s in h.Symbols.FindByName(name))
                matches.Add((h, s));
        if (matches.Count == 0)
            return $"No definition found for \"{name}\". Tips: search_symbols for a partial or one-off name; " +
                   "search_code if it may be a macro/#define, a language without symbol support, or spelled differently." + CoverageCaveat(includeSymbolSkipped: true);

        var sb = new StringBuilder();
        foreach (var (h, s) in matches)
        {
            var path = DisplayPath(h, s.RelativePath);
            string loc = s.EndLine > s.Line ? $"{path}:{s.Line}-{s.EndLine}" : $"{path}:{s.Line}";
            sb.AppendLine($"{loc}:{s.Column}: {s.Kind} {s.Name}");
        }

        // Save the agent a follow-up file read: if there's exactly one match and it's small, inline the
        // definition source right here. Bounded (<= SnippetMaxLines) so the tool result stays cheap.
        if (matches.Count == 1)
        {
            var (h, s) = matches[0];
            var snippet = TryReadSnippet(h.Root, s.RelativePath, s.Line, s.EndLine > s.Line ? s.EndLine : s.Line);
            if (snippet is not null) { sb.AppendLine(); sb.Append(snippet); }
        }
        else sb.Append($"({matches.Count} definitions)");
        return sb.ToString();
    });

    private const int SnippetMaxLines = 40;

    // Read lines [startLine..endLine] (1-based, inclusive) of a repo file for inline display, but only
    // for a small span. Returns null if too big, unreadable, or the file is huge (never materialize a
    // big/streamed file for a snippet). Best-effort - a missing snippet just means "open the file".
    private static string? TryReadSnippet(string root, string relPath, int startLine, int endLine)
    {
        try
        {
            if (endLine < startLine) return null;
            if (endLine - startLine + 1 > SnippetMaxLines) return null; // too big to inline
            if (!CodeCompass.Core.Storage.PathSafety.IsInsideRepo(relPath)) return null; // rel from the index; guard a tampered cache
            var full = System.IO.Path.Combine(root, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            // One open instead of a stat + a separate open: read the size off the open handle (over a
            // share that's one round-trip, not two), then stream just the span we need and stop.
            var network = CodeCompass.Core.Storage.NetworkPath.IsNetwork(root);
            using var fs = CodeCompass.Core.Storage.SourceFile.OpenSequential(full, network);
            if (fs.Length > 8L * 1024 * 1024) return null; // don't crack open large files
            using var reader = new System.IO.StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sb = new StringBuilder();
            int n = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                n++;
                if (n < startLine) continue;
                if (n > endLine) break;
                sb.Append(n).Append(": ").AppendLine(line);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    [McpServerTool(Name = "find_references")]
    [Description("Find where a symbol is used across the codebase. For C# (Roslyn) and C/C++ (clang) " +
                 "this is SEMANTIC - it resolves the actual symbol and ignores matches in comments " +
                 "(including XML-doc <see cref>) and strings. For other languages it falls back to " +
                 "whole-word lexical matches in source files (data/doc files like .json/.csv/.md are " +
                 "not treated as references). Returns ranked 'file:line:col: line'.")]
    public static string FindReferences(
        [Description("Symbol/identifier to find references to (case-sensitive).")] string name,
        [Description("Maximum number of results.")] int maxResults = 100)
        => ServerContext.QueryAll(handles =>
    {
        if (string.IsNullOrWhiteSpace(name)) return "Provide a symbol/identifier to find references to.";
        maxResults = Math.Clamp(maxResults, 1, 1000); // agent-supplied; guard against 0/negative/absurd
        // Collect one past the cap across all sources (C# semantic, C/C++ semantic, then lexical in
        // other files) so truncation is detected by the same overflow probe the other tools use -
        // exact, not a fuzzy threshold. Kind tags let the footer report the shown breakdown. The
        // semantic analyzers span the project + every linked root, so a cross-root reference resolves;
        // the lexical fallback iterates each root's text index (linked hits shown as absolute paths).
        int probe = maxResults + 1;
        var hits = new List<(string Line, char Kind)>();
        // The analyzer spans every root; when a focus is active, keep only hits whose owning root is in scope.
        foreach (var s in ServerContext.CSharp.FindReferences(name, probe, ServerContext.ShutdownToken))
            if (InScope(handles, s.Root))
                hits.Add(($"{DisplayPath(s)}:{s.Line}:{s.Column}: {s.LineText}", 'c'));

        // C/C++ semantic is TARGETED: a reference to `name` can only be in a file whose text contains it,
        // and the trigram index lists exactly those files. So gather the candidate C/C++ sources across all
        // roots (absolute paths) and clang-parses ONLY those - complete, and proportional to the symbol's
        // real footprint, never a whole-tree parse. Empty candidate set => no C/C++ work at all.
        var cppCandidates = new List<string>();
        var csCandidates = new List<string>();
        var csDisplay = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var h in handles)
            foreach (var rel in h.Text.CandidateFiles(name))
            {
                var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(h.Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (IsCppSourceFile(rel)) cppCandidates.Add(full);
                else if (rel.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase)) { csCandidates.Add(full); csDisplay[full] = DisplayPath(h, rel); }
            }
        int cppCand = 0, cppParsed = 0;
        bool cppMemStopped = false, cppTooBroad = false;
        IReadOnlyList<string> cppUnresolved = System.Array.Empty<string>();
        if (cppCandidates.Count > 0)
        {
            // Parse the C/C++ candidates in a SHORT-LIVED CHILD PROCESS so libclang's native memory is
            // reclaimed by the OS when the child exits - this long-lived server otherwise ratchets upward
            // across broad C/C++ queries (native LLVM allocator never returns pages in-process). Falls back
            // to the in-process analyzer on any subprocess failure, so correctness never regresses.
            ClangCppAnalyzer.CppRefResult r;
            var cppRoots = handles.Select(h => h.Root).ToList();
            var worker = ClangSubprocess.Enabled ? ClangSubprocess.WorkerExePath() : null;
            if (ClangSubprocess.Enabled && worker is null)
                CodeCompass.Core.Diagnostics.Log.Global.Warn("clang subprocess enabled but worker exe (CodeCompass.Cli) not found next to the server; using in-process (memory may grow across broad C/C++ queries)");
            if (worker is not null &&
                ClangSubprocess.TryFindReferences(worker, cppRoots, name, cppCandidates, probe, ClangSubprocess.TimeoutSeconds(), out var sub, ServerContext.ShutdownToken))
                r = sub;
            else
                r = ServerContext.Cpp.FindReferencesDetailed(name, cppCandidates, probe, ServerContext.ShutdownToken);
            // A shutdown/re-point may have killed the subprocess (or cancelled the in-process parse) mid-query;
            // surface that as a cancellation so the query abandons cleanly rather than returning a partial result.
            ServerContext.ShutdownToken.ThrowIfCancellationRequested();
            foreach (var s in r.Locations)
                hits.Add(($"{DisplayPath(s)}:{s.Line}:{s.Column}: {s.LineText}", 'p'));
            cppCand = r.CandidateTus; cppParsed = r.ParsedTus; cppUnresolved = r.UnresolvedIncludes;
            cppMemStopped = r.MemoryStopped; cppTooBroad = r.TooManyCandidates;
        }
        // The C/C++ semantic pass is INCOMPLETE when it stopped for memory, some TUs didn't parse, OR there
        // were unresolved #includes (a TU can PARSE with errors yet resolve nothing, so cppParsed==cppCand
        // does NOT mean "fully resolved"). In any of those cases the lexical layer would otherwise drop every
        // C/C++ file (SemanticCoverage treats them as "covered"), yielding a bare "0" on a symbol with real
        // hits. So when incomplete, let lexical cover C/C++ files too, deduped against the semantic hits.
        bool cppIncomplete = SemanticCoverage.IsCppPassIncomplete(cppMemStopped, cppParsed, cppCand, cppUnresolved.Count);
        // Roslyn parses with an empty preprocessor set, so it silently misses references in inactive #if/#elif
        // branches. When any candidate .cs uses conditional compilation, treat the C# pass as incomplete so
        // the lexical backfill covers .cs too (deduped) and we disclose it - the C# twin of cppIncomplete.
        var csConditional = SemanticCoverage.CSharpConditionalFiles(csCandidates);
        bool csharpIncomplete = csConditional.Count > 0;
        // Per-query comment/string classifier so the lexical backfill skips <see cref> doc-comment / string-literal
        // hits in covered-language files (the v1.0.212 precision regression); reads + caches each file once.
        var spanFilter = new LexicalSpanFilter();
        var semKeys = new System.Collections.Generic.HashSet<string>(
            hits.Select(h => { int i = h.Line.IndexOf(": ", System.StringComparison.Ordinal); return i > 0 ? h.Line[..i] : h.Line; }),
            System.StringComparer.OrdinalIgnoreCase);

        if (hits.Count <= maxResults)
            foreach (var h in handles)
            {
                // Reference mode: canonical candidate order (build-order-independent: a local and a UNC index
                // built in separate runs return the same set) + a per-file cap so one high-hit noise file
                // can't consume the whole budget and starve the real references (the UNC refs-count gap).
                foreach (var m in h.Text.Search(name, probe * 5, maxPerFile: ReferenceMerge.MaxLexicalHitsPerFile, orderByPath: true))
                {
                    // Shared filter (same as the CLI): skip semantic-covered files unless that language's pass was
                    // incomplete, skip build noise, require a whole-word match, and skip comment/string spans. The
                    // span filter reads the file, so give it the absolute path; display/dedup keep the relative one.
                    var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(h.Root, m.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    if (!ReferenceMerge.IsLexicalReference(full, m.LineText, m.Column, name.Length, cppIncomplete, csharpIncomplete, spanFilter, m.Line)) continue;
                    var key = $"{DisplayPath(h, m.Path)}:{m.Line}:{m.Column}";
                    if (!semKeys.Add(key)) continue;                             // already found semantically - don't double-count
                    hits.Add(($"{key}: {m.LineText}", 'l'));
                    if (hits.Count > maxResults) break;                          // got the overflow row
                }
                if (hits.Count > maxResults) break;
            }

        // Field diagnostic (opt-in, CODECOMPASS_DEBUG_REFS): the semantic-vs-lexical breakdown that made up
        // this answer, so a UNC refs-count gap can be split into "semantic came back short" vs "lexical
        // dropped hits" against the same query on the real share. No-op unless the flag is set.
        if (CodeCompass.Core.Diagnostics.RefsDebug.On)
            CodeCompass.Core.Diagnostics.RefsDebug.Log(
                $"MCP name='{name}' cppCand={cppCand} cppParsed={cppParsed} memStopped={cppMemStopped} tooBroad={cppTooBroad} " +
                $"unresolvedIncludes={cppUnresolved.Count} cppIncomplete={cppIncomplete} csharpIncomplete={csharpIncomplete} => " +
                $"semC#={hits.Count(h => h.Kind == 'c')} semC/C++={hits.Count(h => h.Kind == 'p')} " +
                $"lexical={hits.Count(h => h.Kind == 'l')} total={hits.Count}");

        // Honest disclosure keyed on what THIS QUERY actually did (not merely whether a compile DB file
        // exists): if some candidate C/C++ TUs failed to parse or had unresolved #includes, a low/zero C/C++
        // count means "couldn't look," not "no references." Names the missing headers - those can't be fixed
        // by any -I/compile DB, only by adding them to the tree. Fires WITH or WITHOUT a compile DB (a present
        // DB must never silence a caveat a failed parse earned).
        string cppNote = "";
        if (cppCandidates.Count > 0)
        {
            var bits = ReferenceMerge.CppCoverageBits(cppParsed, cppCand, cppMemStopped, cppUnresolved, cppTooBroad);
            if (bits.Count > 0)
                cppNote = " (Note: C/C++ coverage INCOMPLETE - " + string.Join("; ", bits) +
                          ". Missing headers aren't in the tree (no -I/compile DB can fix that), so a low or zero " +
                          "C/C++ count may mean 'couldn't parse', not 'no references' - add the headers for full coverage.)";
            else if (!ServerContext.Cpp.HasCompileDb)
                cppNote = " (Note: no compile_commands.json found - C/C++ references resolved with best-effort " +
                          "flags and may be imprecise; add one for precise results.)";
        }
        // C# conditional-compilation disclosure: Roslyn can't see inactive #if/#elif branches, so a semantic
        // count may miss #if-guarded references (shown lexically where the backfill found them).
        string csNote = csharpIncomplete
            ? " (Note: " + ReferenceMerge.CSharpConditionalNote(
                  csConditional.Select(f => csDisplay.TryGetValue(f, out var disp) ? disp : System.IO.Path.GetFileName(f)).ToList()) + ")"
            : "";

        if (hits.Count == 0)
            return $"No references found for \"{name}\". Tip: try search_code for a raw text search " +
                   "(it may not resolve as a symbol here), or check the exact spelling/case." + cppNote + csNote + CoverageCaveat();

        bool truncated = hits.Count > maxResults;
        var shown = hits.Take(maxResults).ToList();
        var sb = new StringBuilder();
        foreach (var (line, _) in shown) sb.AppendLine(line);
        int cs = shown.Count(h => h.Kind == 'c'), cpp = shown.Count(h => h.Kind == 'p'), lex = shown.Count(h => h.Kind == 'l');
        sb.Append($"({cs} C# + {cpp} C/C++ semantic reference(s); {lex} lexical in other files)");
        if (truncated) sb.Append(" - MORE EXIST, narrow the query or raise the limit");
        sb.Append(cppNote);
        sb.Append(csNote);
        return sb.ToString();
    });

    [McpServerTool(Name = "find_callees")]
    [Description("List the in-repo methods a C# method CALLS (its callees), resolved SEMANTICALLY: " +
                 "overloads bind to the real declaration and framework/external calls are omitted, so " +
                 "you get the true call targets - not every same-named symbol a syntactic graph returns. " +
                 "Each result is the callee's DEFINITION (file:line-endLine), so you can walk a call chain " +
                 "downward one hop at a time without reading each body. C# only; other languages return " +
                 "nothing - use find_definition then read.")]
    public static string FindCallees(
        [Description("Exact C# method name (case-sensitive).")] string name,
        [Description("Maximum number of callees.")] int maxResults = 50)
        => ServerContext.QueryAll(handles =>
    {
        if (string.IsNullOrWhiteSpace(name)) return "Provide a C# method name.";
        maxResults = Math.Clamp(maxResults, 1, 1000); // agent-supplied; guard against 0/negative/absurd
        // Callees are resolved across the project + linked roots (the analyzer spans them all), so a call
        // chain that crosses into a linked root is walkable; each callee is shown at its owning root.
        // The analyzer spans every root; under an active focus keep only callees whose DEFINITION is in scope.
        var callees = ServerContext.CSharp.FindCallees(name, maxResults + 1, ServerContext.ShutdownToken)
            .Where(c => InScope(handles, c.Root)).ToList();
        // Conditional-compilation handling (see find_references): FindCallees walks the method body via Roslyn,
        // which parses with an empty preprocessor set and can't see inactive #if/#elif branches - so a call
        // guarded by conditional compilation is SILENTLY missing (the forward/reverse asymmetry: refs finds the
        // edge, callees doesn't). When the method's file(s) use conditional compilation, recover those calls by
        // re-lexing the disabled regions and resolving each name in-repo - segregated + labeled "by name" since
        // disabled text can't be semantically bound - and name the file(s) in the disclosure.
        var calleeCsCands = new List<string>();
        var calleeCsDisplay = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var h in handles)
            foreach (var rel in h.Text.CandidateFiles(name))
                if (rel.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase))
                {
                    var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(h.Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    calleeCsCands.Add(full); calleeCsDisplay[full] = DisplayPath(h, rel);
                }
        var calleeCond = SemanticCoverage.CSharpConditionalFiles(calleeCsCands);
        var recovered = calleeCond.Count > 0
            ? ServerContext.CSharp.FindCalleesInInactiveBranches(name, maxResults + 1, ServerContext.ShutdownToken)
                .Where(r => InScope(handles, r.Root)).ToList()
            : (IReadOnlyList<SemanticLocation>)System.Array.Empty<SemanticLocation>();
        // Dedup recovered vs the FULL semantic list (incl. any truncated overflow row), so a call present in both
        // an active and an inactive branch is never shown twice - and the disclosure counts what's actually shown.
        var extraCallees = new List<SemanticLocation>();
        if (recovered.Count > 0)
        {
            var semKeys = new System.Collections.Generic.HashSet<string>(
                callees.Select(c => $"{DisplayPath(c)}:{c.Line}:{c.Column}"), System.StringComparer.OrdinalIgnoreCase);
            extraCallees = recovered.Where(r => semKeys.Add($"{DisplayPath(r)}:{r.Line}:{r.Column}")).Take(maxResults).ToList();
        }
        string calleeCsNote = calleeCond.Count > 0
            ? " (Note: " + ReferenceMerge.CSharpConditionalCalleesNote(
                  calleeCond.Select(f => calleeCsDisplay.TryGetValue(f, out var d) ? d : System.IO.Path.GetFileName(f)).ToList(), extraCallees.Count) + ")"
            : "";
        if (callees.Count == 0 && extraCallees.Count == 0)
            // Distinguish "no such symbol" from "found, but calls no repo code" - answering a bare
            // "nothing" to both is the silent-empty hazard that turns a typo into a false finding.
            return (handles.Any(h => h.Symbols.FindByName(name).Count > 0)
                ? $"\"{name}\" is defined here, but calls no in-repo methods - it may call only " +
                  "framework/external code, or it isn't C# (callees are semantic for C# only)."
                : $"No symbol named \"{name}\" is indexed - check the exact spelling/case, or it may be a " +
                  "macro or an unsupported language. (find_callees resolves C# only.)") + calleeCsNote + CoverageCaveat();

        bool truncated = callees.Count > maxResults;
        var sb = new StringBuilder();
        foreach (var c in callees.Take(maxResults)) sb.AppendLine($"{DisplayPath(c)}:{c.Line}:{c.Column}: {c.LineText}");
        sb.Append(Footer(Math.Min(callees.Count, maxResults), truncated, "callee", "callees"));
        // Segregated #if-recovered callees (resolved by name), already deduped against the full semantic list.
        if (extraCallees.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(ReferenceMerge.CSharpInactiveCalleesHeader(extraCallees.Count));
            foreach (var r in extraCallees) sb.AppendLine($"{DisplayPath(r)}:{r.Line}:{r.Column}: {r.LineText}");
        }
        sb.Append(calleeCsNote);
        return sb.ToString();
    });

    [McpServerTool(Name = "search_symbols")]
    [Description("Search symbol names by case-insensitive substring. " +
                 "Returns 'file:line:col: Kind Name'. Use this to discover related definitions.")]
    public static string SearchSymbols(
        [Description("Substring to match against symbol names (case-insensitive).")] string query,
        [Description("Maximum number of results.")] int maxResults = 50)
        => ServerContext.QueryAll(handles =>
    {
        if (string.IsNullOrWhiteSpace(query)) return "Provide a symbol-name substring to search for.";
        maxResults = Math.Clamp(maxResults, 1, 1000); // agent-supplied; guard against 0/negative/absurd
        var matches = new List<(ServerContext.IndexHandle H, Symbol S)>();
        foreach (var h in handles)
        {
            foreach (var s in h.Symbols.Find(query, maxResults + 1)) matches.Add((h, s));
            if (matches.Count > maxResults) break;
        }
        if (matches.Count == 0)
            return $"No symbols matching \"{query}\". Tip: try search_code for a text search " +
                   "(it may not be a captured symbol - e.g. a macro, or an unsupported language)." + CoverageCaveat(includeSymbolSkipped: true);

        bool truncated = matches.Count > maxResults;
        var sb = new StringBuilder();
        foreach (var (h, s) in matches.Take(maxResults)) sb.AppendLine($"{DisplayPath(h, s.RelativePath)}:{s.Line}:{s.Column}: {s.Kind} {s.Name}");
        sb.Append(Footer(Math.Min(matches.Count, maxResults), truncated, "symbol", "symbols"));
        return sb.ToString();
    });

    [McpServerTool(Name = "reindex")]
    [Description("Rebuild the CodeCompass index for this workspace from scratch. Also reports index " +
                 "status. Run this after large external changes (e.g. a source-control sync) if results seem stale.")]
    public static string Reindex()
    {
        // A synchronous rebuild of a large/network workspace would block this tool call for minutes - the MCP
        // host times out the call while the build keeps running (orphaned), the very stall the deferred-to-CLI
        // policy exists to prevent. Refuse it and point at the CLI, consistent with the initial-index deferral.
        if (ServerContext.ReindexWouldExceedAutoLimit(out _)) return ServerContext.CliBuildGuidance();
        var s = ServerContext.Rebuild();
        return $"Reindexed {s.Files} files ({s.Bytes / (1024.0 * 1024.0):F1} MB) in {s.Seconds:F2}s; " +
               $"{s.Symbols} symbols.";
    }

    [McpServerTool(Name = "manage_links")]
    [Description("Configure LINKED ROOTS - external directories outside this workspace (a shared library, a " +
                 "sibling repo, a third-party drop on another drive) whose code is federated into this " +
                 "workspace's searches. action: \"list\" (default) shows the linked roots, index status, and " +
                 "the current focus; \"add\" attaches a directory (and indexes it, or defers if very large); " +
                 "\"remove\" detaches one; \"focus\" SCOPES searches to a subset of roots (see below). 'path' " +
                 "is the external directory (required for add/remove). Changes take effect on the NEXT query " +
                 "this session. Searching across links is otherwise automatic.\n" +
                 "FOCUS - when you link several big repos into one workspace but want to search only one at a " +
                 "time: action=\"focus\" path=\"<repo>\" scopes every subsequent search to that root (the " +
                 "wrapper project + any other linked roots are excluded); 'path' matches a repo folder name, a " +
                 "path fragment, or a full path, and accepts a comma-separated list to focus several at once. " +
                 "action=\"focus\" with NO path clears it (searches federate across all roots again). Scoped " +
                 "results disclose what was excluded, so a narrowed search is never mistaken for 'not found'.")]
    public static string ManageLinks(
        [Description("What to do: list | add | remove | focus (default list).")] string action = "list",
        [Description("For add/remove: the external directory (absolute path). For focus: repo name(s)/path(s) to scope to (comma-separated); omit to clear focus.")] string? path = null,
        [Description("On remove, also delete the linked root's index if no other workspace uses it (reclaim disk).")] bool purge = false)
    {
        var project = ServerContext.Root;
        if (string.IsNullOrWhiteSpace(project)) return "No workspace is currently being served.";

        switch ((action ?? "list").Trim().ToLowerInvariant())
        {
            case "list":
            {
                var links = LinkManager.List(project);
                var focus = ServerContext.CurrentFocus;
                if (links.Count == 0)
                    return $"No linked roots for {project}. Use action=\"add\" with a path to federate an external directory." +
                           (focus.Count > 0 ? $"\n(Focus is set to {focus.Count} root(s), but there are no links.)" : "");
                var sb = new StringBuilder($"Linked roots for {project}:\n");
                foreach (var l in links) sb.AppendLine($"  {l.Path}  [{l.Status}]");
                sb.Append(focus.Count > 0
                    ? $"Focus: ACTIVE - searches scoped to {focus.Count} root(s): {string.Join(", ", focus)}. Clear with action=focus (no path)."
                    : "Focus: off - searches federate across the project + all linked roots. Scope to one with action=focus path=\"<repo>\".");
                return sb.ToString().TrimEnd();
            }
            case "focus":
            {
                // The focusable universe: the project (wrapper) root + every configured linked root.
                var universe = new List<string> { project };
                universe.AddRange(LinkStore.Read(project));
                if (string.IsNullOrWhiteSpace(path))
                {
                    ServerContext.ClearFocus();
                    return $"Focus cleared - searches federate across all {universe.Count} root(s) again.";
                }
                var tokens = path.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var matched = new List<string>();
                var unmatched = new List<string>();
                foreach (var tok in tokens)
                {
                    var found = CodeCompass.Core.Storage.RootScope.Match(universe, tok);
                    if (found.Count == 0) unmatched.Add(tok);
                    else matched.AddRange(found);
                }
                if (unmatched.Count > 0)
                    return $"No root matches: {string.Join(", ", unmatched)}. Known roots: {string.Join(", ", universe)}. " +
                           "Focus unchanged - pass a repo folder name, a path fragment, or an absolute path.";
                matched = matched.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                ServerContext.SetFocus(matched);
                int excluded = universe.Count - matched.Count;
                return excluded > 0
                    ? $"Focused on {matched.Count} of {universe.Count} root(s): {string.Join(", ", matched)}. " +
                      $"{excluded} root(s) excluded from searches until you clear focus (action=focus, no path)."
                    : $"Focus set to all {universe.Count} root(s) - nothing is excluded (clear with action=focus, no path).";
            }
            case "add":
            {
                if (string.IsNullOrWhiteSpace(path)) return "Provide 'path' - the external directory to link.";
                // A relative path would resolve against the SERVER process's working directory (wherever it was
                // launched), not this workspace - almost never what the caller means. Require an absolute path so
                // the linked root is deterministic (the tool contract already says "absolute path").
                if (!System.IO.Path.IsPathRooted(path)) return $"Provide an ABSOLUTE path (got relative '{path}'). Relative paths resolve against the server's launch directory, not this workspace.";
                var r = LinkManager.Add(project, path);
                var suffix = r.Status is LinkManager.AddStatus.Rejected or LinkManager.AddStatus.AlreadyLinked ? "" : " — active on the next query.";
                return r.Message + suffix;
            }
            case "remove":
            {
                if (string.IsNullOrWhiteSpace(path)) return "Provide 'path' - the linked directory to remove.";
                if (!System.IO.Path.IsPathRooted(path)) return $"Provide an ABSOLUTE path (got relative '{path}'). Relative paths resolve against the server's launch directory, not this workspace.";
                var r = LinkManager.Remove(project, path, _ => purge);
                var sb = new StringBuilder(r.Message);
                foreach (var o in r.OtherProjects) sb.Append($"\n      {o}");
                if (r.Status != LinkManager.RemoveStatus.NotLinked) sb.Append(" — active on the next query.");
                return sb.ToString();
            }
            default:
                return $"Unknown action '{action}'. Use list, add, remove, or focus.";
        }
    }
}
