using System.Text.Json;
using ClangSharp;
using ClangSharp.Interop;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Walking;

namespace CodeCompass.Semantics;

/// <summary>
/// Precise C/C++ code intelligence via clang (libclang through ClangSharp). It parses
/// each translation unit once, keying declarations by USR (clang's stable, cross-TU
/// symbol identity) so references resolve to the actual symbol - matches in comments
/// and strings are never counted. Uses compile_commands.json when present (root or
/// root/build) for accurate include paths/defines; otherwise best-effort default flags.
///
/// Built once, lazily, and cached. Rebuild by creating a new instance. The keyed declaration/reference
/// maps hold the whole C/C++ semantic model in memory, so a long-lived server disposes it when idle to
/// reclaim that RAM (see ServerContext).
///
/// It can span several roots (a project plus its linked external roots): every root's translation units
/// are parsed into one USR-keyed model, so a reference in one root to a symbol defined in another is
/// captured (subject to include paths resolving across the boundary). Each result carries the absolute
/// root it belongs to (see <see cref="SemanticLocation.Root"/>) so callers can address it correctly.
/// </summary>
public sealed class ClangCppAnalyzer : IDisposable
{
    private static readonly HashSet<string> SourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx", ".c++" };

    private readonly IReadOnlyList<string> _roots; // absolute; [0] is the primary (project) root
    private readonly object _gate = new();
    private bool _built;

    private readonly Dictionary<string, HashSet<string>> _usrsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Loc>> _defsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Loc>> _refsByUsr = new(StringComparer.Ordinal);
    private Dictionary<string, string[]>? _compileArgs;

    // Parse accounting for honest reporting: how many C/C++ translation units we attempted vs. actually
    // parsed, and whether a compile_commands.json was found. Without one, best-effort flags leave many TUs
    // unparsed (empty USR maps), so a bare "0 references" would read as "none exist" rather than "the
    // semantic layer couldn't run here" - the tools surface these so that zero is honest. Valid after build.
    private bool _hasCompileDb;
    private int _tuSeen, _tuParsed;

    /// <summary>Build accounting so callers can disclose a degraded C/C++ semantic result honestly.</summary>
    public readonly record struct ParseStats(int SourceFilesSeen, int SourceFilesParsed, bool HasCompileDb);
    public ParseStats Stats { get { EnsureBuilt(); return new ParseStats(_tuSeen, _tuParsed, _hasCompileDb); } }

    private readonly record struct Loc(string Full, int Line, int Column); // Full = absolute path

    public ClangCppAnalyzer(string root) : this(new[] { root }) { }

    /// <summary>Span multiple roots (project + linked external roots) in one semantic model, so references
    /// resolve across the boundary. The first root is treated as primary by callers for path display.</summary>
    public ClangCppAnalyzer(IReadOnlyList<string> roots) =>
        _roots = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToList();

    /// <summary>Release the in-memory semantic model. Safe to call while the instance is being
    /// discarded; a fresh instance rebuilds lazily on next use.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _usrsByName.Clear();
            _defsByName.Clear();
            _refsByUsr.Clear();
            _compileArgs = null;
            _hasCompileDb = false;
            _tuSeen = 0;
            _tuParsed = 0;
            _built = false;
        }
    }

    public IReadOnlyList<SemanticLocation> FindDefinitions(string name)
    {
        EnsureBuilt();
        var result = new List<SemanticLocation>();
        if (_defsByName.TryGetValue(name, out var locs))
        {
            var seen = new HashSet<Loc>();
            var lineCache = new Dictionary<string, string[]>(StringComparer.Ordinal); // per-query; freed on return
            foreach (var l in locs)
                if (seen.Add(l)) result.Add(ToSemantic(l, lineCache));
        }
        return result;
    }

    public IReadOnlyList<SemanticLocation> FindReferences(string name, int max = 200)
    {
        EnsureBuilt();
        var result = new List<SemanticLocation>();
        if (!_usrsByName.TryGetValue(name, out var usrs)) return result;

        var seen = new HashSet<Loc>();
        var lineCache = new Dictionary<string, string[]>(StringComparer.Ordinal); // per-query; freed on return
        foreach (var usr in usrs)
        {
            if (!_refsByUsr.TryGetValue(usr, out var locs)) continue;
            foreach (var l in locs)
            {
                if (seen.Add(l))
                {
                    result.Add(ToSemantic(l, lineCache));
                    if (result.Count >= max) return result;
                }
            }
        }
        return result;
    }

    private void EnsureBuilt()
    {
        lock (_gate)
        {
            if (_built) return;
            _built = true;

            LoadCompileCommands();

            var walker = new FileWalker(new IgnoreRules());
            using var index = CXIndex.Create();
            foreach (var root in _roots)
            foreach (var file in walker.Walk(root))
            {
                if (!SourceExtensions.Contains(Path.GetExtension(file.RelativePath))) continue;
                _tuSeen++;
                try { if (ParseFile(index, file.FullPath)) _tuParsed++; }
                catch { /* skip files clang can't handle */ }
            }
        }
    }

    // Returns true if the translation unit actually parsed (so the caller can count parse coverage - a
    // low parsed/seen ratio with no compile DB is the "semantics couldn't run here" signal the tools show).
    private bool ParseFile(CXIndex index, string fullPath)
    {
        var args = GetArgs(fullPath);
        var error = CXTranslationUnit.TryParse(index, fullPath, args,
            ReadOnlySpan<CXUnsavedFile>.Empty,
            CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord,
            out CXTranslationUnit tu);
        if (error != CXErrorCode.CXError_Success) return false;

        var translationUnit = TranslationUnit.GetOrCreate(tu);
        Walk(translationUnit.TranslationUnitDecl);
        return true;
    }

    private void Walk(Cursor cursor)
    {
        Visit(cursor.Handle);
        foreach (var child in cursor.CursorChildren)
            Walk(child);
    }

    private void Visit(CXCursor h)
    {
        var spelling = h.Spelling.ToString();

        if (!string.IsNullOrEmpty(spelling) && clang.isDeclaration(h.Kind) != 0)
        {
            var usr = h.Usr.ToString();
            if (!string.IsNullOrEmpty(usr))
            {
                if (!_usrsByName.TryGetValue(spelling, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _usrsByName[spelling] = set;
                }
                set.Add(usr);

                if (h.IsDefinition && TryLoc(h.Location, out var defLoc))
                    Add(_defsByName, spelling, defLoc);
            }
        }

        var referenced = clang.getCursorReferenced(h);
        if (clang.Cursor_isNull(referenced) == 0 &&
            clang.equalLocations(h.Location, referenced.Location) == 0)
        {
            var usr = referenced.Usr.ToString();
            if (!string.IsNullOrEmpty(usr) && TryLoc(h.Location, out var refLoc))
                Add(_refsByUsr, usr, refLoc);
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

    private void LoadCompileCommands()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            string? path = null;
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "compile_commands.json"),
                         Path.Combine(root, "build", "compile_commands.json"),
                     })
            {
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            if (path is null) continue;

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

                    List<string> tokens;
                    if (entry.TryGetProperty("arguments", out var argsArr) && argsArr.ValueKind == JsonValueKind.Array)
                        tokens = argsArr.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                    else if (entry.TryGetProperty("command", out var cmd) && cmd.GetString() is string c)
                        tokens = c.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    else
                        continue;

                    map[full] = CleanArgs(tokens, fileName); // later roots don't clobber earlier files (distinct keys)
                }
            }
            catch { /* malformed DB in this root: fall back to defaults for its files */ }
        }
        if (map.Count > 0) { _compileArgs = map; _hasCompileDb = true; }
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
