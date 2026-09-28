using System.Linq;
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
/// proportional to how much the symbol is actually used (a handful of TUs), never a full-tree parse, with
/// no arbitrary cap and nothing dropped. The heavy lifting (indexing all text) is done once at
/// `codecompass index` time; this layer just rides that index. Each parse is per-query and disposed
/// immediately, so memory stays bounded to a few TUs. Callers supply the candidate files (from the trigram
/// index); with none supplied it self-scans the tree (used by unit tests).
///
/// It can span several roots (a project plus its linked external roots): candidate TUs from any root are
/// parsed into one USR model, so a reference in one root to a symbol defined in another resolves. Each
/// result carries the absolute root it belongs to (see <see cref="SemanticLocation.Root"/>).
/// </summary>
public sealed class ClangCppAnalyzer : IDisposable
{
    private static readonly HashSet<string> SourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx", ".c++" };

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
    private static long MaxTuBytes()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_MAX_TU_MB");
        if (long.TryParse(env, out var mb) && mb > 0) return mb * 1024 * 1024;
        return 2L * 1024 * 1024;
    }

    // True if this source file is too large for a bounded semantic parse - skip clang, let the lexical
    // reference layer (memory-bounded) handle it. Stat failure -> not skipped (let the parse attempt decide).
    private static bool TooBigForClang(string full)
    {
        try { return new FileInfo(full).Length > MaxTuBytes(); } catch { return false; }
    }

    // How many candidate translation units to parse concurrently. Each parse can hold a large AST, so bound
    // the degree by AVAILABLE memory (NOT total - a busy box has far less real headroom; total was the hole
    // that let peak reach 16 GB) over a conservative per-TU reserve, capped at the core count and a modest
    // ceiling. Combined with the per-TU size cap above, worst-case peak is (degree x capped-TU). Env
    // CODECOMPASS_CPP_PARSE_THREADS overrides.
    private static int ParseDegree()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_PARSE_THREADS");
        if (int.TryParse(env, out var n) && n > 0) return n;
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
        public readonly HashSet<string> UnresolvedIncludes = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A C/C++ reference result plus the coverage the query actually achieved. UnresolvedIncludes
    /// names headers clang couldn't find (missing from the tree - unfixable by any -I/compile DB), so a
    /// partial or empty result can be disclosed honestly instead of read as a confident zero.</summary>
    public readonly record struct CppRefResult(
        IReadOnlyList<SemanticLocation> Locations, int CandidateTus, int ParsedTus, IReadOnlyList<string> UnresolvedIncludes,
        bool MemoryStopped = false);

    public ClangCppAnalyzer(string root) : this(new[] { root }) { }

    /// <summary>Span multiple roots (project + linked external roots). The first root is primary (callers
    /// use it for path display).</summary>
    public ClangCppAnalyzer(IReadOnlyList<string> roots) =>
        _roots = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToList();

    /// <summary>Whether a compile_commands.json was found for any root. Cheap (no parsing). Callers use it
    /// to disclose that C/C++ resolution is best-effort without one.</summary>
    public bool HasCompileDb { get { EnsureCompileDb(); return _hasCompileDb; } }

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
    public IReadOnlyList<SemanticLocation> FindReferences(string name, IReadOnlyCollection<string>? candidateFiles = null, int max = 200)
        => FindReferencesDetailed(name, candidateFiles, max).Locations;

    /// <summary>As <see cref="FindReferences"/> but also returns the coverage the query achieved (candidate
    /// TUs, how many parsed, unresolved includes) so callers can disclose a partial/empty result honestly.</summary>
    public CppRefResult FindReferencesDetailed(string name, IReadOnlyCollection<string>? candidateFiles = null, int max = 200)
    {
        var model = BuildModel(name, candidateFiles);
        var result = new List<SemanticLocation>();
        if (model.UsrsByName.TryGetValue(name, out var usrs))
        {
            var seen = new HashSet<Loc>();
            var lineCache = new Dictionary<string, string[]>(StringComparer.Ordinal); // per-query; freed on return
            foreach (var usr in usrs)
            {
                if (!model.RefsByUsr.TryGetValue(usr, out var locs)) continue;
                foreach (var l in locs)
                    if (seen.Add(l))
                    {
                        result.Add(ToSemantic(l, lineCache));
                        if (result.Count >= max) { return Cover(model, result); }
                    }
            }
        }
        return Cover(model, result);
    }

    private static CppRefResult Cover(Model m, List<SemanticLocation> locs) =>
        new(locs, m.Candidates, m.Parsed, m.UnresolvedIncludes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), m.MemoryStopped);

    /// <summary>Definitions of the C/C++ symbol <paramref name="name"/>. See <see cref="FindReferences"/>
    /// for <paramref name="candidateFiles"/>.</summary>
    public IReadOnlyList<SemanticLocation> FindDefinitions(string name, IReadOnlyCollection<string>? candidateFiles = null)
    {
        var model = BuildModel(name, candidateFiles);
        var result = new List<SemanticLocation>();
        if (model.DefsByName.TryGetValue(name, out var locs))
        {
            var seen = new HashSet<Loc>();
            var lineCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var l in locs)
                if (seen.Add(l)) result.Add(ToSemantic(l, lineCache));
        }
        return result;
    }

    // Parse just the files that could contain the query into a fresh model. Bounded to the candidate set,
    // so memory and time scale with the symbol's actual footprint, not the repo size. Candidate TUs are
    // parsed CONCURRENTLY (each is an independent parse - the dominant cost on register-heavy files), into
    // thread-local models merged at the end; degree is RAM-bounded so peak stays in check.
    private Model BuildModel(string name, IReadOnlyCollection<string>? candidateFiles)
    {
        EnsureCompileDb();
        var files = ResolveFiles(name, candidateFiles).ToList();
        var model = new Model { Candidates = files.Count };
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
        var sessMb = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB");
        if (long.TryParse(sessMb, out var sm) && sm > 0) absCeiling = sm * 1024 * 1024;
        else absCeiling = Math.Clamp(totalRam / 2, 2L << 30, 32L << 30);

        // Per-QUERY growth cap: bounds how much ONE query may grow the working set, so a single broad sweep
        // can't consume the whole session ceiling and starve later queries. Scales with free commit; env-
        // overridable. Upper bound tracks the session ceiling (was a flat 1.5 GB, which on a big box limited
        // even the FIRST cold query to ~14 of 881 candidates).
        long growthBudget;
        var envMb = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB");
        if (long.TryParse(envMb, out var mb) && mb > 0) growthBudget = mb * 1024 * 1024;
        else growthBudget = Math.Clamp(SystemMemory.AvailableCommitBytes() / 4, 512L << 20, absCeiling);

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
            if (OverBudget()) model.MemoryStopped = true;
            else { try { ParseInto(files[0], model); } catch { } }
            return model;
        }

        var opts = new ParallelOptions { MaxDegreeOfParallelism = ParseDegree() };
        bool stopped = false; // set (idempotently) when the budget trips; read after the loop joins
        Parallel.ForEach(files, opts,
            () => new Model(),                                         // thread-local model
            (full, loopState, local) =>
            {
                if (loopState.ShouldExitCurrentIteration) return local;
                if (OverBudget()) { stopped = true; loopState.Stop(); return local; } // stop scheduling more TUs
                try { ParseInto(full, local); } catch { }
                return local;
            },
            local => { lock (_mergeLock) { MergeInto(model, local); } });
        if (stopped) model.MemoryStopped = true;
        return model;
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
    private IEnumerable<string> ResolveFiles(string name, IReadOnlyCollection<string>? candidateFiles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (candidateFiles is not null)
        {
            foreach (var f in candidateFiles)
            {
                string full;
                try { full = Path.GetFullPath(f); } catch { continue; }
                if (!SourceExtensions.Contains(Path.GetExtension(full))) continue; // headers are parsed via #include
                if (OwnerOf(full) is null) continue;                               // must be under a root
                if (seen.Add(full) && File.Exists(full) && !TooBigForClang(full)) yield return full;
            }
        }
        else
        {
            var walker = new FileWalker(new IgnoreRules());
            foreach (var root in _roots)
                foreach (var file in walker.Walk(root))
                {
                    if (!SourceExtensions.Contains(Path.GetExtension(file.RelativePath))) continue;
                    if (seen.Add(file.FullPath) && !TooBigForClang(file.FullPath) && FileContains(file.FullPath, name)) yield return file.FullPath;
                }
        }
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
            CXTranslationUnit_Flags.CXTranslationUnit_None,
            out CXTranslationUnit tu);
        if (error != CXErrorCode.CXError_Success) return; // TU couldn't be produced at all - not counted as parsed

        // From here the raw native `tu` (the single largest allocation in this system - up to ~1 GB) is owned
        // by nobody until TranslationUnit.GetOrCreate wraps it. If the wrap (or CollectUnresolvedIncludes)
        // throws - e.g. GetOrCreate's allocation under memory pressure, exactly the giant-TU case - the raw
        // handle would leak. Track whether the managed wrapper took ownership: dispose the wrapper if it did
        // (which disposes the handle), else dispose the raw handle directly. Never both -> no double-free.
        model.Parsed++;
        TranslationUnit? translationUnit = null;
        try
        {
            CollectUnresolvedIncludes(tu, model); // names of #includes clang couldn't find (missing from the tree)
            lock (_clangGate) { translationUnit = TranslationUnit.GetOrCreate(tu); }
            Walk(translationUnit.TranslationUnitDecl, model);
        }
        finally
        {
            lock (_clangGate)
            {
                if (translationUnit is not null) translationUnit.Dispose(); // disposes the underlying handle
                else { try { tu.Dispose(); } catch { } }                    // wrap never happened - free the raw TU
            }
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

    private void Walk(Cursor cursor, Model model)
    {
        Visit(cursor.Handle, model);
        foreach (var child in cursor.CursorChildren)
            Walk(child, model);
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
                            tokens = c.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
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

    // Drop the compiler executable, the source file, and output/compile-step flags;
    // keep include paths, defines, std, etc. that clang needs to bind correctly.
    private static string[] CleanArgs(List<string> tokens, string fileName)
    {
        var result = new List<string>();
        var baseName = Path.GetFileName(fileName);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (i == 0) continue;                             // compiler executable
            if (t is "-c") continue;
            if (t is "-o") { i++; continue; }                 // skip -o <output>
            if (t.EndsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue; // the source file
            result.Add(t);
        }
        return result.ToArray();
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
        if (loc.Line >= 1 && loc.Line <= lines.Length)
            text = lines[loc.Line - 1].Trim();

        var (root, rel) = OwnerOf(loc.Full) ?? ("", loc.Full.Replace('\\', '/'));
        return new SemanticLocation(rel, loc.Line, loc.Column, text, root);
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
