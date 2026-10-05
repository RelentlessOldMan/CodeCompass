using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClangSharp;
using ClangSharp.Interop;
using CodeCompass.Core.Config;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Walking;

namespace CodeCompass.Semantics;

/// <summary>
/// Precise C/C++ code intelligence via clang (libclang through ClangSharp). Declarations are keyed by USR
/// (clang's stable, cross-TU symbol identity) so references resolve to the actual symbol - matches in
/// comments and strings are never counted. Uses compile_commands.json when present (root, root/build, or a
/// configured location) for accurate include paths/defines; otherwise best-effort default flags.
///
/// TARGETED, not whole-repo. A reference to <c>Foo</c> can only live in a file whose text contains "Foo",
/// and the trigram index already knows which files those are - so a query parses ONLY those candidate
/// translation units, not the entire tree. That makes find-references on a 40k-file repo cost work
/// proportional to how much the symbol is actually used (a handful of TUs), never a full-tree parse. The one
/// bound is a LATENCY guard (see <see cref="MaxSemanticCandidates"/>): when a supplied candidate set runs to
/// hundreds of TUs (a pathologically-broad symbol) the semantic parse is skipped and the caller's lexical
/// backfill answers instead (disclosed), rather than grinding for minutes to the same lexical result. The
/// heavy lifting (indexing all text) is done once at
/// `codecompass index` time; this layer just rides that index. Each parse is per-query and disposed
/// immediately, so memory stays bounded to a few TUs. Callers supply the candidate files (from the trigram
/// index); with none supplied it self-scans the tree (used by unit tests, and the CLI on an unindexed root -
/// the self-scan path has no lexical backfill, so the latency guard above never fires there).
///
/// It can span several roots (a project plus its linked external roots): candidate TUs from any root are
/// parsed into one USR model, so a reference in one root to a symbol defined in another resolves. Each
/// result carries the absolute root it belongs to (see <see cref="SemanticLocation.Root"/>).
/// </summary>
public sealed class ClangCppAnalyzer : IDisposable
{
    private static readonly HashSet<string> SourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx", ".c++" };

    /// <summary>Is this a C/C++ translation unit (a file clang parses directly; headers come in via #include)? The one
    /// definition callers use to pick C/C++ candidates for a targeted find_references.</summary>
    public static bool IsCppSource(string path) => SourceExtensions.Contains(Path.GetExtension(path));

    private readonly IReadOnlyList<string> _roots; // absolute; [0] is the primary (project) root
    private readonly object _gate = new();
    private readonly object _mergeLock = new();  // guards merging a thread-local model into the shared one
    private readonly object _clangGate = new();  // serializes ClangSharp's static TU cache (GetOrCreate/Dispose)
    private Dictionary<string, string[]>? _compileArgs;
    private bool _hasCompileDb;
    private bool _dbLoaded;

    // Max source-file size handed to clang for a semantic parse. A multi-MB C/C++ SOURCE file is
    // generated/pathological, and its translation unit can balloon to GBs of AST - parsing several such TUs
    // at once is what drove a broad find_references to ~16 GB (a token referenced across 200x 2-8 MB files,
    // ~4 GB per TU x degree 4). Above this cap the file is left to the LEXICAL reference layer (still found,
    // just not clang-confirmed), so memory stays bounded without dropping results. Env CODECOMPASS_CPP_MAX_TU_MB.
    internal static long MaxTuBytes() => CodeCompassConfig.CppMaxTuBytes();

    // True if this source file is too large for a bounded semantic parse - skip clang, let the lexical
    // reference layer (memory-bounded) handle it. Stat failure -> not skipped (let the parse attempt decide).
    private static bool TooBigForClang(string full)
    {
        try { return new FileInfo(full).Length > MaxTuBytes(); } catch { return false; }
    }

    // How many candidate TUs a single query may parse semantically before we skip the semantic pass ENTIRELY
    // and let the (fast, index-driven) lexical layer answer. This is a LATENCY guard, distinct from the memory
    // budget below (which bounds PEAK RSS): a symbol referenced across hundreds of candidate TUs would grind a
    // semantic parse for minutes, hit the per-query memory budget partway, and fall back to lexical for the
    // remainder anyway - so the semantic work is pure wasted latency for a result the lexical backfill produces
    // in ~2 s. (Field-observed on a large generated corpus over UNC: 1,405 candidates -> 253 parsed in 131 s ->
    // 0 semantic refs kept.) Above this count we short-circuit to lexical up front and disclose it. The default
    // sits well above any real symbol's direct-reference footprint (a handful to a few dozen TUs) yet below the
    // generated-corpus pathologies. Env CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES; 0 disables the guard (always
    // attempt semantic, the pre-guard behaviour).
    private static int MaxSemanticCandidates() => CodeCompassConfig.CppMaxSemanticCandidates();

    // How many candidate translation units to parse concurrently. Each parse can hold a large AST, so bound
    // the degree by AVAILABLE memory (NOT total - a busy box has far less real headroom; total was the hole
    // that let peak reach 16 GB) over a conservative per-TU reserve, capped at the core count and a modest
    // ceiling. Combined with the per-TU size cap above, worst-case peak is (degree x capped-TU). Env
    // CODECOMPASS_CPP_PARSE_THREADS overrides.
    private static int ParseDegree()
    {
        if (CodeCompassConfig.CppParseThreads() is int n) return n;
        long avail = CodeCompass.Core.Diagnostics.SystemMemory.AvailableCommitBytes();
        if (avail <= 0) { try { avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { avail = 8L * 1024 * 1024 * 1024; } }
        int byMem = (int)Math.Max(1, (avail / 2) / (1500L * 1024 * 1024));
        return Math.Clamp(Math.Min(Environment.ProcessorCount, byMem), 1, 3);
    }

    private readonly record struct Loc(string Full, int Line, int Column); // Full = absolute path

    // The per-query semantic model: only the candidate TUs for one query are parsed into it, then it's
    // discarded. Local (not instance) state, so concurrent queries on a shared analyzer can't race.
    private sealed class Model
    {
        public readonly Dictionary<string, HashSet<string>> UsrsByName = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<Loc>> DefsByName = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<Loc>> RefsByUsr = new(StringComparer.Ordinal);
        // Per-query coverage accounting, so callers can DISCLOSE why a result is partial/empty: how many
        // candidate TUs we tried, how many parsed, and which #includes clang couldn't find (missing headers
        // - the case where a bare "0 references" is really "couldn't look," not "no callers").
        public int Candidates;
        public int Parsed;
        public bool MemoryStopped; // the semantic pass hit its memory budget and stopped before parsing every candidate
        public bool TooManyCandidates; // the candidate set exceeded the semantic-parse limit, so the pass was skipped up front (latency guard)
        public int SkippedTooBig;      // candidate sources over the per-TU size cap, left to the lexical layer
        public readonly HashSet<string> UnresolvedIncludes = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A C/C++ reference result plus the coverage the query actually achieved. UnresolvedIncludes
    /// names headers clang couldn't find (missing from the tree - unfixable by any -I/compile DB), so a
    /// partial or empty result can be disclosed honestly instead of read as a confident zero.</summary>
    public readonly record struct CppRefResult(
        IReadOnlyList<SemanticLocation> Locations, int CandidateTus, int ParsedTus, IReadOnlyList<string> UnresolvedIncludes,
        bool MemoryStopped = false, bool TooManyCandidates = false, int SkippedTooBig = 0, bool WorkerFailed = false,
        bool WorkerStalled = false);

    /// <summary>Invoked after each candidate TU finishes (parsed or not). The isolated worker turns this into a heartbeat
    /// so its parent kills only a STALLED worker - never a slow but progressing one. Null (no-op) in-process.</summary>
    public static Action? TuFinished;

    public ClangCppAnalyzer(string root) : this(new[] { root }) { }

    /// <summary>Span multiple roots (project + linked external roots). The first root is primary (callers
    /// use it for path display).</summary>
    public ClangCppAnalyzer(IReadOnlyList<string> roots) =>
        _roots = roots.Select(PathSafety.NormalizeDir).ToList();

    /// <summary>Whether a compile_commands.json was found for any root. Cheap (no parsing). Callers use it
    /// to disclose that C/C++ resolution is best-effort without one.</summary>
    public bool HasCompileDb { get { EnsureCompileDb(); return _hasCompileDb; } }

    /// <summary>The same question without an analyzer instance: does any root have a compile_commands.json to use? (A
    /// file-existence probe - for wording a disclosure, it must not construct and pin a whole analyzer.)</summary>
    public static bool ProbeCompileDb(IEnumerable<string> roots)
    {
        try { return roots.Any(r => CodeCompassConfig.CompileCommandsFiles(r, CodeCompassConfig.ReadFrom(r)).Any()); }
        catch { return false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _compileArgs = null;
            _hasCompileDb = false;
            _dbLoaded = false;
        }
    }

    /// <summary>True (semantic) references to the C/C++ symbol <paramref name="name"/>. <paramref
    /// name="candidateFiles"/> is the set of files that might contain it (absolute paths, from the trigram
    /// index); only those TUs are parsed. Null => self-scan the tree (unit tests / no index available).</summary>
    public IReadOnlyList<SemanticLocation> FindReferences(string name, IReadOnlyCollection<string>? candidateFiles = null, int max = 200, System.Threading.CancellationToken ct = default)
        => FindReferencesDetailed(name, candidateFiles, max, ct).Locations;

    /// <summary>As <see cref="FindReferences"/> but also returns the coverage the query achieved (candidate
    /// TUs, how many parsed, unresolved includes) so callers can disclose a partial/empty result honestly.</summary>
    public CppRefResult FindReferencesDetailed(string name, IReadOnlyCollection<string>? candidateFiles = null, int max = 200, System.Threading.CancellationToken ct = default)
    {
        var model = BuildModel(name, candidateFiles, ct);
        var result = new List<SemanticLocation>();
        if (model.UsrsByName.TryGetValue(name, out var usrs))
        {
            // DETERMINISTIC: candidate TUs are parsed in parallel and merged in completion order, and USRs live in a
            // hash set - so emit in a canonical (path, line, column) order BEFORE truncating at `max`, or two identical
            // queries could return different reference SETS.
            var all = new HashSet<Loc>();
            foreach (var usr in usrs)
                if (model.RefsByUsr.TryGetValue(usr, out var locs)) all.UnionWith(locs);
            var lineCache = new Dictionary<string, string[]>(StringComparer.Ordinal); // per-query; freed on return
            foreach (var l in Canonical(all))
            {
                result.Add(ToSemantic(l, lineCache));
                if (result.Count >= max) break;
            }
        }
        return Cover(model, result);
    }

    private static IEnumerable<Loc> Canonical(IEnumerable<Loc> locs) =>
        locs.OrderBy(l => l.Full, StringComparer.Ordinal).ThenBy(l => l.Line).ThenBy(l => l.Column);

    private static CppRefResult Cover(Model m, List<SemanticLocation> locs) =>
        new(locs, m.Candidates, m.Parsed, m.UnresolvedIncludes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), m.MemoryStopped, m.TooManyCandidates, m.SkippedTooBig);

    /// <summary>Definitions of the C/C++ symbol <paramref name="name"/>. See <see cref="FindReferences"/>
    /// for <paramref name="candidateFiles"/>. Test oracle only: find_definition uses the tree-sitter symbol index -
    /// don't wire a tool to this assuming parity.</summary>
    internal IReadOnlyList<SemanticLocation> FindDefinitions(string name, IReadOnlyCollection<string>? candidateFiles = null)
    {
        var model = BuildModel(name, candidateFiles);
        var result = new List<SemanticLocation>();
        if (model.DefsByName.TryGetValue(name, out var locs))
        {
            var lineCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var l in Canonical(new HashSet<Loc>(locs))) result.Add(ToSemantic(l, lineCache));
        }
        return result;
    }

    // Parse just the files that could contain the query into a fresh model. Bounded to the candidate set,
    // so memory and time scale with the symbol's actual footprint, not the repo size. Candidate TUs are
    // parsed CONCURRENTLY (each is an independent parse - the dominant cost on register-heavy files), into
    // thread-local models merged at the end; degree is RAM-bounded so peak stays in check.
    private Model BuildModel(string name, IReadOnlyCollection<string>? candidateFiles, System.Threading.CancellationToken ct = default)
    {
        EnsureCompileDb();

        // Pathologically-broad short-circuit (LATENCY guard, see MaxSemanticCandidates): a huge candidate set
        // would grind for minutes, blow the memory budget partway, and fall back to lexical anyway. Skip the
        // semantic parse entirely (Parsed stays 0, so IsCppPassIncomplete is true and the caller's lexical
        // backfill answers fast) and flag it so the disclosure explains the deliberate skip.
        // ONLY skip when the caller SUPPLIED a candidate list (the indexed MCP / CLI / subprocess-worker path).
        // Two reasons this is gated on candidateFiles != null:
        //  1. Safety net - that path has a LEXICAL backfill to catch the references we're skipping. The self-scan
        //     path (candidateFiles == null: unit tests, or a CLI `refs` on an UNINDEXED root) has NO lexical
        //     fallback, so skipping there would turn a slow-but-complete answer into a bare zero. It must parse.
        //  2. Cost - the supplied count is an in-memory number from the trigram index, so we bail BEFORE
        //     ResolveFiles stats each candidate (over UNC that's ~2 network round trips per file - pointless work
        //     when we're about to skip). A post-ResolveFiles count would also be redundant: ResolveFiles only
        //     ever shrinks the set, so anything it could catch is already caught here.
        int maxSem = MaxSemanticCandidates();
        if (maxSem > 0 && candidateFiles is not null && candidateFiles.Count > maxSem)
            return new Model { Candidates = candidateFiles.Count, TooManyCandidates = true };

        var files = ResolveFiles(name, candidateFiles, out int skippedTooBig);
        // A size-skipped candidate is NOT silently "covered": it makes the pass incomplete (the caller's lexical
        // backfill then searches it) and is disclosed - see SemanticCoverage.IsCppPassIncomplete.
        var model = new Model { Candidates = files.Count, SkippedTooBig = skippedTooBig };
        if (files.Count == 0) return model;

        // Per-QUERY memory budget. clang TU memory is NATIVE and, empirically, is not returned to the OS as we
        // parse successive candidate TUs - so a broad query over many ordinary sources grows ~linearly and
        // would eventually exhaust RAM (a common symbol across 800+ files climbed past 9 GB on a real tree).
        // Stop parsing further candidates once the process has grown past a budget (or free commit runs low),
        // and disclose the partial coverage. This bounds peak regardless of candidate count.
        long baseWs = CurrentWorkingSetBytes();
        long totalRam; try { totalRam = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { totalRam = 8L << 30; }
        if (totalRam <= 0) totalRam = 8L << 30;

        // Absolute session ceiling on total process working set from clang's (native, not-returned-to-OS) TU
        // memory. Bounds the long-lived server ACROSS queries. Scales with total RAM and is env-overridable.
        // (v1.0.175/176 clamped this to 3 GB regardless of box RAM; the real-tree field report 2026-09-28
        // showed that on a 32 GB box the session crossed 3 GB after one or two broad queries and OverBudget()
        // then returned true before parsing a SINGLE candidate - a near-total downgrade to lexical-only.
        // Scale with RAM instead; the free-commit Floor below is the real hard OOM guard, this is the coarse cap.)
        long absCeiling;
        if (CodeCompassConfig.CppSessionMemMb() is long sm) absCeiling = sm * 1024 * 1024;
        else absCeiling = Math.Clamp(totalRam / 2, 2L << 30, 32L << 30);

        // Per-QUERY growth cap: bounds how much ONE query may grow the working set, so a single broad sweep
        // can't consume the whole session ceiling and starve later queries. Scales with free commit; env-
        // overridable. Upper bound tracks the session ceiling (was a flat 1.5 GB, which on a big box limited
        // even the FIRST cold query to ~14 of 881 candidates).
        long growthBudget;
        if (CodeCompassConfig.CppQueryMemMb() is long mb) growthBudget = mb * 1024 * 1024;
        else
        {
            // Floor is normally 512 MB, but never above the session ceiling - otherwise a small
            // CODECOMPASS_CPP_SESSION_MEM_MB (< 512 MB) would make Clamp's min exceed its max and throw.
            long gbFloor = Math.Min(512L << 20, absCeiling);
            growthBudget = Math.Clamp(SystemMemory.AvailableCommitBytes() / 4, gbFloor, absCeiling);
        }

        const long Floor = 1536L * 1024 * 1024; // never drive free commit below this (the true OOM guard)
        bool OverBudget()
        {
            long ws = CurrentWorkingSetBytes();
            if (ws - baseWs > growthBudget) return true;   // per-query growth cap
            if (ws > absCeiling) return true;               // absolute session cap (stops cross-query ratchet)
            long avail = SystemMemory.AvailableCommitBytes();
            return avail > 0 && avail < Floor;              // real memory pressure at check-time (true OOM guard)
        }

        if (files.Count == 1)
        {
            ct.ThrowIfCancellationRequested(); // shutdown/re-point: abort before a (possibly long) single-TU parse
            if (OverBudget()) model.MemoryStopped = true;
            else { try { ParseInto(files[0], model); } catch (Exception ex) { LogParseFailure(files[0], ex); } }
            return model;
        }

        // CancellationToken: a shutdown/re-point cancels the server token so an in-flight C/C++ parse bails
        // (ParallelOptions throws OperationCanceledException) instead of pegging cores while teardown waits.
        var opts = new ParallelOptions { MaxDegreeOfParallelism = ParseDegree(), CancellationToken = ct };
        bool stopped = false; // set (idempotently) when the budget trips; read after the loop joins
        Parallel.ForEach(files, opts,
            () => new Model(),                                         // thread-local model
            (full, loopState, local) =>
            {
                if (loopState.ShouldExitCurrentIteration) return local;
                if (OverBudget()) { stopped = true; loopState.Stop(); return local; } // stop scheduling more TUs
                try { ParseInto(full, local); } catch (Exception ex) { LogParseFailure(full, ex); }
                return local;
            },
            local => { lock (_mergeLock) { MergeInto(model, local); } });
        if (stopped) model.MemoryStopped = true;
        return model;
    }

    // A TU whose parse or AST walk threw is NOT counted as parsed (Parsed is bumped only after the walk), so the pass
    // reads as incomplete and the lexical backfill covers it - this only makes the failure visible in the log.
    private static void LogParseFailure(string full, Exception ex)
    {
        try { Log.Global.Warn($"C/C++ semantic parse failed for {full}: {ex.GetType().Name}: {ex.Message} (left to the lexical layer)"); } catch { }
    }

    // Current process working set (native + managed) - the signal that actually reflects clang's native TU
    // memory, which GC.GetGCMemoryInfo does not see. Snapshot; cheap enough to poll per candidate TU.
    private static long CurrentWorkingSetBytes()
    {
        try { return Environment.WorkingSet; } catch { return 0; }
    }

    // Merge a thread-local model into the shared one (caller holds _mergeLock).
    private static void MergeInto(Model dst, Model src)
    {
        foreach (var kv in src.UsrsByName)
        {
            if (!dst.UsrsByName.TryGetValue(kv.Key, out var set)) { set = new HashSet<string>(StringComparer.Ordinal); dst.UsrsByName[kv.Key] = set; }
            set.UnionWith(kv.Value);
        }
        foreach (var kv in src.DefsByName)
        {
            if (!dst.DefsByName.TryGetValue(kv.Key, out var list)) { list = new List<Loc>(); dst.DefsByName[kv.Key] = list; }
            list.AddRange(kv.Value);
        }
        foreach (var kv in src.RefsByUsr)
        {
            if (!dst.RefsByUsr.TryGetValue(kv.Key, out var list)) { list = new List<Loc>(); dst.RefsByUsr[kv.Key] = list; }
            list.AddRange(kv.Value);
        }
        dst.Parsed += src.Parsed;
        dst.UnresolvedIncludes.UnionWith(src.UnresolvedIncludes);
    }

    // The absolute source files to parse for this query: the supplied candidates (filtered to C/C++ sources
    // under one of our roots), or - when none are supplied - a self-scan that reads each source file's text
    // and keeps those containing the name (the fallback for unit tests / callers without a trigram index).
    private List<string> ResolveFiles(string name, IReadOnlyCollection<string>? candidateFiles, out int skippedTooBig)
    {
        var result = new List<string>();
        skippedTooBig = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (candidateFiles is not null)
        {
            foreach (var f in candidateFiles)
            {
                string full;
                try { full = Path.GetFullPath(f); } catch { continue; }
                if (!SourceExtensions.Contains(Path.GetExtension(full))) continue; // headers are parsed via #include
                if (OwnerOf(full) is null) continue;                               // must be under a root
                if (!seen.Add(full) || !File.Exists(full)) continue;
                if (TooBigForClang(full)) { skippedTooBig++; continue; }
                result.Add(full);
            }
        }
        else
        {
            var walker = new FileWalker(new IgnoreRules());
            foreach (var root in _roots)
                foreach (var file in walker.Walk(root))
                {
                    if (!SourceExtensions.Contains(Path.GetExtension(file.RelativePath))) continue;
                    if (!seen.Add(file.FullPath) || !FileContains(file.FullPath, name)) continue;
                    if (TooBigForClang(file.FullPath)) { skippedTooBig++; continue; }
                    result.Add(file.FullPath);
                }
        }
        return result;
    }

    // Cheap text pre-filter for the self-scan fallback: does the file contain the name at all? (A real
    // reference must.) A file too large to grep cheaply is included rather than risk missing a reference.
    private static bool FileContains(string full, string name)
    {
        try
        {
            var fi = new FileInfo(full);
            if (!fi.Exists) return false;
            if (fi.Length > 32L * 1024 * 1024) return true; // too big to read cheaply - include to stay complete
            return File.ReadAllText(full).Contains(name, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // Parses one TU on its OWN CXIndex (so concurrent parses are independent) into the given model, then
    // disposes the TU immediately so memory never holds more than the in-flight TUs. ClangSharp's static
    // TU cache (GetOrCreate/Dispose) is the only cross-thread shared state, so it's serialized by _clangGate;
    // the parse itself and the per-instance cursor walk run concurrently.
    private void ParseInto(string fullPath, Model model)
    {
        using var index = CXIndex.Create();
        var args = GetArgs(fullPath);
        var error = CXTranslationUnit.TryParse(index, fullPath, args,
            ReadOnlySpan<CXUnsavedFile>.Empty,
            // KeepGoing: don't abort the whole parse at the first FATAL diagnostic (typically a missing #include).
            // Without it clang stops emitting AST at the fatal error, so any reference AFTER it in the TU is
            // silently lost; with it the parse continues and those references are still captured (the unresolved
            // #include is still recorded + disclosed via CollectUnresolvedIncludes). A completeness win for messy/
            // partial trees - deterministic, no latency change, same references a clean parse would find plus the
            // ones that used to fall off the cliff after a fatal error.
            CXTranslationUnit_Flags.CXTranslationUnit_KeepGoing,
            out CXTranslationUnit tu);
        if (error != CXErrorCode.CXError_Success) { TuFinished?.Invoke(); return; } // TU couldn't be produced at all - not counted as parsed

        // From here the raw native `tu` (the single largest allocation in this system - up to ~1 GB) is owned
        // by nobody until TranslationUnit.GetOrCreate wraps it. If the wrap (or CollectUnresolvedIncludes)
        // throws - e.g. GetOrCreate's allocation under memory pressure, exactly the giant-TU case - the raw
        // handle would leak. Track whether the managed wrapper took ownership: dispose the wrapper if it did
        // (which disposes the handle), else dispose the raw handle directly. Never both -> no double-free.
        TranslationUnit? translationUnit = null;
        try
        {
            CollectUnresolvedIncludes(tu, model); // names of #includes clang couldn't find (missing from the tree)
            lock (_clangGate) { translationUnit = TranslationUnit.GetOrCreate(tu); }
            Walk(translationUnit.TranslationUnitDecl, model);
            // Counted only once its references are fully collected: a walk that throws leaves Parsed < Candidates,
            // which marks the pass incomplete (lexical backfill + disclosure) instead of a silent partial "complete".
            model.Parsed++;
        }
        finally
        {
            lock (_clangGate)
            {
                if (translationUnit is not null) translationUnit.Dispose(); // disposes the underlying handle
                else { try { tu.Dispose(); } catch { } }                    // wrap never happened - free the raw TU
            }
            TuFinished?.Invoke();
        }
    }

    // Scan this TU's diagnostics for "'X.h' file not found" and record X - the headers absent from the tree,
    // which no -I set or compile DB can fix and which are the usual reason a C/C++ reference query comes back
    // empty. Per-TU handle, so safe to call concurrently.
    private static readonly System.Text.RegularExpressions.Regex FileNotFound =
        new("'([^']+)' file not found", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static unsafe void CollectUnresolvedIncludes(CXTranslationUnit tu, Model model)
    {
        try
        {
            uint n = clang.getNumDiagnostics(tu);
            for (uint i = 0; i < n; i++)
            {
                var d = clang.getDiagnostic(tu, i);
                try
                {
                    var m = FileNotFound.Match(clang.getDiagnosticSpelling(d).ToString());
                    if (m.Success) model.UnresolvedIncludes.Add(Path.GetFileName(m.Groups[1].Value));
                }
                finally { clang.disposeDiagnostic(d); }
            }
        }
        catch { /* diagnostics are best-effort; a failure just omits the disclosure */ }
    }

    // Pre-order DFS with an explicit stack: recursion to AST depth can overflow the stack on a pathologically nested
    // (generated or hostile) TU - an uncatchable StackOverflowException that would take down the long-lived server on
    // the in-process path. Children are pushed in reverse so the visit order matches the recursive walk exactly.
    private void Walk(Cursor root, Model model)
    {
        var stack = new Stack<Cursor>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cursor = stack.Pop();
            Visit(cursor.Handle, model);
            var children = cursor.CursorChildren;
            for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    private void Visit(CXCursor h, Model model)
    {
        var spelling = h.Spelling.ToString();

        if (!string.IsNullOrEmpty(spelling) && clang.isDeclaration(h.Kind) != 0)
        {
            var usr = h.Usr.ToString();
            if (!string.IsNullOrEmpty(usr))
            {
                if (!model.UsrsByName.TryGetValue(spelling, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    model.UsrsByName[spelling] = set;
                }
                set.Add(usr);

                if (h.IsDefinition && TryLoc(h.Location, out var defLoc))
                    Add(model.DefsByName, spelling, defLoc);
            }
        }

        var referenced = clang.getCursorReferenced(h);
        if (clang.Cursor_isNull(referenced) == 0 &&
            clang.equalLocations(h.Location, referenced.Location) == 0)
        {
            var usr = referenced.Usr.ToString();
            if (!string.IsNullOrEmpty(usr) && TryLoc(h.Location, out var refLoc))
                Add(model.RefsByUsr, usr, refLoc);
        }
    }

    private static void Add(Dictionary<string, List<Loc>> map, string key, Loc loc)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<Loc>();
            map[key] = list;
        }
        list.Add(loc);
    }

    private unsafe bool TryLoc(CXSourceLocation location, out Loc loc)
    {
        loc = default;
        CXFile file;
        uint line, column, offset;
        clang.getExpansionLocation(location, (void**)&file, &line, &column, &offset);

        var name = clang.getFileName(file).ToString();
        if (string.IsNullOrEmpty(name)) return false;

        string full;
        try { full = Path.GetFullPath(name); }
        catch { return false; }

        if (OwnerOf(full) is null) return false; // skip system headers / anything outside our roots

        loc = new Loc(full, (int)line, (int)column);
        return true;
    }

    private string[] GetArgs(string fullPath)
    {
        if (_compileArgs is not null && _compileArgs.TryGetValue(Path.GetFullPath(fullPath), out var a))
            return a;
        var dir = Path.GetDirectoryName(fullPath) ?? _roots[0];
        // Pick the dialect by extension: a .c file compiled as -std=c++17 fails on valid C (implicit
        // void* conversions, C-only keywords, identifiers that are C++ reserved words), which would empty
        // the semantic model on exactly the C firmware repos least likely to ship a compile DB. gnu11
        // matches the GNU extensions those toolchains assume.
        bool isC = Path.GetExtension(fullPath).Equals(".c", StringComparison.OrdinalIgnoreCase);
        var args = new List<string> { isC ? "-std=gnu11" : "-std=c++17", "-I" + dir };
        foreach (var root in _roots) args.Add("-I" + root); // let includes resolve across every root
        return args.ToArray();
    }

    private void EnsureCompileDb()
    {
        lock (_gate)
        {
            if (_dbLoaded) return;
            _dbLoaded = true;
            LoadCompileCommands();
        }
    }

    private void LoadCompileCommands()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            // Every compile DB for this root, configured locations first (so an explicit choice wins a
            // per-file collision), then the conventional root / root/build. Merged per source file, so a
            // multi-target build that emits one DB per target is fully covered by their union.
            foreach (var path in CodeCompassConfig.CompileCommandsFiles(root, CodeCompassConfig.ReadFrom(root)))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var entry in doc.RootElement.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("file", out var fileProp)) continue;
                        var fileName = fileProp.GetString();
                        if (string.IsNullOrEmpty(fileName)) continue;

                        var dir = entry.TryGetProperty("directory", out var d) ? d.GetString() : null;
                        var full = Path.GetFullPath(dir is null ? fileName : Path.Combine(dir, fileName));
                        if (map.ContainsKey(full)) continue; // first DB to describe a file wins (explicit > auto)

                        List<string> tokens;
                        if (entry.TryGetProperty("arguments", out var argsArr) && argsArr.ValueKind == JsonValueKind.Array)
                            tokens = argsArr.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                        else if (entry.TryGetProperty("command", out var cmd) && cmd.GetString() is string c)
                            tokens = TokenizeCommand(c);
                        else
                            continue;

                        map[full] = CleanArgs(tokens, fileName);
                    }
                    _hasCompileDb = true; // a DB was found and parsed (even if it listed no usable entries)
                }
                catch { /* malformed DB: skip it, fall back to defaults for its files */ }
            }
        }
        if (map.Count > 0) _compileArgs = map;
    }

    // Split a compile_commands.json "command" string into argv, honoring double quotes and backslash escapes.
    // A naive Split(' ') shreds the common Windows case -I "C:\Program Files\..." into broken tokens, so the
    // include path never resolves and the TU parses with errors (honest-but-wrong "0 refs / unresolved include").
    internal static List<string> TokenizeCommand(string command)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < command.Length; i++)
        {
            char ch = command[i];
            if (ch == '"') { inQuotes = !inQuotes; any = true; }
            else if (ch == '\\' && i + 1 < command.Length && command[i + 1] == '"') { sb.Append('"'); i++; any = true; } // \" -> literal quote
            else if (!inQuotes && (ch == ' ' || ch == '\t'))
            {
                if (any) { tokens.Add(sb.ToString()); sb.Clear(); any = false; }
            }
            else { sb.Append(ch); any = true; }
        }
        if (any) tokens.Add(sb.ToString());
        return tokens;
    }

    // Flags that take their value as the NEXT token when not joined (-DX vs -D X), kept with that value.
    private static readonly HashSet<string> KeptWithValue = new(StringComparer.Ordinal)
    {
        "-D", "-U", "-I", "-isystem", "-iquote", "-idirafter", "-include", "-imacros", "-isysroot", "--sysroot",
        "-iprefix", "-iwithprefix", "-iwithprefixbefore", "-x", "-target", "-arch",
    };

    // Joined-form prefixes of the same flags (-DX, -I/path, -std=c++17, --target=...), kept as-is.
    private static readonly string[] KeptPrefixes =
    {
        "-D", "-U", "-I", "-isystem", "-iquote", "-idirafter", "-isysroot", "--sysroot=", "-std=", "--std=",
        "--target=", "-x", "-W", "-m", "-f", "-O",
    };

    // Dropped together with their next-token value: they forward raw args, load code, remap the filesystem, or
    // write files (-Xclang -load <dll>, -ivfsoverlay <overlay>, -MF <depfile>, -B <tool dir>, ...).
    private static readonly HashSet<string> DroppedWithValue = new(StringComparer.Ordinal)
    {
        "-Xclang", "-Xpreprocessor", "-Xlinker", "-Xassembler", "-MF", "-MT", "-MQ", "-ivfsoverlay", "-B",
        "-include-pch", "-load", "-o", "-mllvm",
    };

    // -W/-f/-m forms that are NOT parse options: argument forwarding (-Wp,-MD,<file> writes a file), plugin/module
    // loading, and every output-writing knob.
    private static readonly string[] DroppedSubstrings =
    {
        "plugin", "module", "crash", "profile", "coverage", "save-", "time-trace", "dump", "record", "instrument",
        "build-session", "embed", "depfile", "stack-usage", "callgraph",
    };

    /// <summary>
    /// Turn a compile_commands.json entry's argv into the args handed to libclang - as an ALLOWLIST. The compile DB
    /// comes from the repo being indexed, which is untrusted: passing it through let a hostile clone have clang load
    /// a plugin DLL (-Xclang -load, -fplugin=), remap files (-ivfsoverlay), or write files anywhere (-MF,
    /// -fmodules-cache-path, -Wp,-MD). Only options that change how the code PARSES survive: defines, include
    /// paths, forced includes, language/standard, target, and the -f/-m/-W/-O dialect knobs minus the dangerous
    /// ones above. The compiler, the source file, -c/-o and anything unrecognized are dropped. MSVC-style
    /// /I /D /U /FI /std: are translated (passed through raw, clang took them as extra input files and failed the
    /// whole translation unit).
    /// </summary>
    internal static string[] CleanArgs(List<string> tokens, string fileName)
    {
        var result = new List<string>();
        // MSVC spellings only from an MSVC driver: on a Unix compile DB a positional /Data/x.c must never be read as /D.
        bool msvc = tokens.Count > 0 && IsMsvcDriver(tokens[0]);
        for (int i = 1; i < tokens.Count; i++) // [0] is the compiler executable
        {
            var t = tokens[i];
            if (t.Length == 0 || t == "-c") continue;
            bool hasNext = i + 1 < tokens.Count;

            if (DroppedWithValue.Contains(t)) { i++; continue; }
            if (KeptWithValue.Contains(t))
            {
                if (hasNext) { result.Add(t); result.Add(tokens[++i]); }
                continue;
            }

            if (t[0] == '/')
            {
                if (!msvc) continue;
                // cl accepts both /DNAME and /D NAME.
                if (t is "/D" or "/U" or "/I" or "/FI")
                {
                    if (hasNext) result.Add(TranslateMsvc(t + tokens[++i])!);
                    continue;
                }
                if (TranslateMsvc(t) is string translated) result.Add(translated);
                continue;
            }
            if (t[0] != '-') continue; // positional (the source file itself, a stray input, an @rsp file): drop

            if (t.StartsWith("-Wp,", StringComparison.Ordinal) || t.StartsWith("-Wl,", StringComparison.Ordinal) ||
                t.StartsWith("-Wa,", StringComparison.Ordinal)) continue; // raw argument forwarding (-Wp,-MD,<file> writes)
            if (t.StartsWith("-M", StringComparison.Ordinal)) continue;      // dependency-file generation (writes files)
            if (t.StartsWith("-include", StringComparison.Ordinal) || t.StartsWith("-imacros", StringComparison.Ordinal))
            {
                // Joined form (-include<file>); the separate form and -include-pch were handled above.
                if (t.Length > 8 && !t.StartsWith("-include-pch", StringComparison.Ordinal)) result.Add(t);
                continue;
            }
            if (t is "-pthread" or "-nostdinc" or "-nostdinc++" or "-nostdlibinc" or "-nobuiltininc" or "-undef"
                  or "-trigraphs" or "-ansi" or "-pedantic") { result.Add(t); continue; }

            bool kept = false;
            foreach (var p in KeptPrefixes) if (t.StartsWith(p, StringComparison.Ordinal)) { kept = true; break; }
            if (!kept) continue;
            if (t.StartsWith("-f", StringComparison.Ordinal) || t.StartsWith("-m", StringComparison.Ordinal) ||
                t.StartsWith("-W", StringComparison.Ordinal))
                foreach (var s in DroppedSubstrings)
                    if (t.Contains(s, StringComparison.OrdinalIgnoreCase)) { kept = false; break; }
            if (kept) result.Add(t);
        }
        return result.ToArray();
    }

    private static bool IsMsvcDriver(string compiler)
    {
        var name = Path.GetFileNameWithoutExtension(compiler.Trim('"')).ToLowerInvariant();
        return name is "cl" or "clang-cl";
    }

    // /Ipath /Dname /Uname /FIfile /std:c++17 (cl.exe / clang-cl compile DBs) -> the GNU spellings libclang parses.
    private static string? TranslateMsvc(string t)
    {
        if (t.Length > 3 && t.StartsWith("/FI", StringComparison.Ordinal)) return "-include" + t[3..];
        if (t.StartsWith("/std:", StringComparison.Ordinal)) return "-std=" + t[5..];
        if (t.Length > 2 && (t[1] == 'I' || t[1] == 'D' || t[1] == 'U')) return "-" + t[1] + t[2..];
        return null;
    }

    // lineCache is passed in per query and discarded when the query returns, so scattered references in
    // one file still read it once, without the analyzer holding whole files resident for the session.
    private SemanticLocation ToSemantic(Loc loc, Dictionary<string, string[]> lineCache)
    {
        var text = "";
        if (!lineCache.TryGetValue(loc.Full, out var lines))
        {
            try { lines = File.ReadAllLines(loc.Full); }
            catch { lines = Array.Empty<string>(); }
            lineCache[loc.Full] = lines;
        }
        int column = loc.Column;
        if (loc.Line >= 1 && loc.Line <= lines.Length)
        {
            var raw = lines[loc.Line - 1];
            column = CharColumn(raw, loc.Column);
            text = CodeCompass.Core.Text.LineSnippet.Make(raw, Math.Clamp(column - 1, 0, raw.Length), 1).Text.Trim(); // bounded + scrubbed
        }

        var (root, rel) = OwnerOf(loc.Full) ?? ("", loc.Full.Replace('\\', '/'));
        return new SemanticLocation(rel, loc.Line, column, text, root);
    }

    /// <summary>libclang reports columns in UTF-8 BYTES; everything else (the lexical layer, editors) counts UTF-16
    /// characters. Convert, so a non-ASCII prefix on the line (an accented comment, a non-ASCII string) doesn't put
    /// the semantic hit at a different column than the lexical one - which broke dedup and listed it twice.</summary>
    internal static int CharColumn(string line, int byteColumn1)
    {
        int targetBytes = byteColumn1 - 1, bytes = 0, i = 0;
        while (i < line.Length && bytes < targetBytes)
        {
            char c = line[i];
            if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) { bytes += 4; i += 2; continue; }
            // U+FFFD usually stands for ONE undecodable byte (a non-UTF-8 file), not its 3-byte UTF-8 encoding.
            bytes += c < 0x80 || c == '\uFFFD' ? 1 : c < 0x800 ? 2 : 3;
            i++;
        }
        return i + 1;
    }

    // Map an absolute path to its (owning root, forward-slash relative path), or null if it's under none of
    // our roots (a system header). For a single-root analyzer Root is empty and Rel is the FileWalker
    // relative path, so single-root behaviour (and its tests) are unchanged.
    private (string Root, string Rel)? OwnerOf(string fullPath)
    {
        for (int i = 0; i < _roots.Count; i++)
        {
            if (!PathSafety.IsUnderOrEqual(fullPath, _roots[i])) continue;
            var rel = Path.GetRelativePath(_roots[i], fullPath).Replace('\\', '/');
            return (i == 0 ? "" : _roots[i], rel); // primary root -> empty (path is repo-relative)
        }
        return null;
    }
}
