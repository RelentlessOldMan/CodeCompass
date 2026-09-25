using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClangSharp;
using ClangSharp.Interop;
using CodeCompass.Core.Config;
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

    // How many candidate translation units to parse concurrently. Each giant-include TU can hold ~1 GB while
    // parsing, so bound the degree so peak stays sane: ~half of available RAM divided by a ~1.2 GB per-TU
    // reserve, capped at the core count and a modest ceiling. Env CODECOMPASS_CPP_PARSE_THREADS overrides.
    private static int ParseDegree()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_PARSE_THREADS");
        if (int.TryParse(env, out var n) && n > 0) return n;
        long avail;
        try { avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { avail = 8L * 1024 * 1024 * 1024; }
        int byMem = (int)Math.Max(1, (avail / 2) / (1200L * 1024 * 1024));
        // Cap at 4 by default: memory was the reported bug, so keep worst-case peak modest (~4 giant TUs ~4 GB)
        // while still giving a big speedup on many-includer queries. Raise via CODECOMPASS_CPP_PARSE_THREADS.
        return Math.Clamp(Math.Min(Environment.ProcessorCount, byMem), 1, 4);
    }

    private readonly record struct Loc(string Full, int Line, int Column); // Full = absolute path

    // The per-query semantic model: only the candidate TUs for one query are parsed into it, then it's
    // discarded. Local (not instance) state, so concurrent queries on a shared analyzer can't race.
    private sealed class Model
    {
        public readonly Dictionary<string, HashSet<string>> UsrsByName = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<Loc>> DefsByName = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<Loc>> RefsByUsr = new(StringComparer.Ordinal);
    }

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
    {
        var model = BuildModel(name, candidateFiles);
        var result = new List<SemanticLocation>();
        if (!model.UsrsByName.TryGetValue(name, out var usrs)) return result;

        var seen = new HashSet<Loc>();
        var lineCache = new Dictionary<string, string[]>(StringComparer.Ordinal); // per-query; freed on return
        foreach (var usr in usrs)
        {
            if (!model.RefsByUsr.TryGetValue(usr, out var locs)) continue;
            foreach (var l in locs)
                if (seen.Add(l))
                {
                    result.Add(ToSemantic(l, lineCache));
                    if (result.Count >= max) return result;
                }
        }
        return result;
    }

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
        var model = new Model();
        if (files.Count == 0) return model;
        if (files.Count == 1) { try { ParseInto(files[0], model); } catch { } return model; }

        var opts = new ParallelOptions { MaxDegreeOfParallelism = ParseDegree() };
        Parallel.ForEach(files, opts,
            () => new Model(),                                         // thread-local model
            (full, _, local) => { try { ParseInto(full, local); } catch { } return local; },
            local => { lock (_mergeLock) { MergeInto(model, local); } });
        return model;
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
                if (seen.Add(full) && File.Exists(full)) yield return full;
            }
        }
        else
        {
            var walker = new FileWalker(new IgnoreRules());
            foreach (var root in _roots)
                foreach (var file in walker.Walk(root))
                {
                    if (!SourceExtensions.Contains(Path.GetExtension(file.RelativePath))) continue;
                    if (seen.Add(file.FullPath) && FileContains(file.FullPath, name)) yield return file.FullPath;
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
        if (error != CXErrorCode.CXError_Success) return;

        TranslationUnit translationUnit;
        lock (_clangGate) { translationUnit = TranslationUnit.GetOrCreate(tu); }
        try { Walk(translationUnit.TranslationUnitDecl, model); }
        finally { lock (_clangGate) { translationUnit.Dispose(); } }
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
