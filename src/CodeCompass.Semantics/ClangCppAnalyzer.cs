using System.Text.Json;
using ClangSharp;
using ClangSharp.Interop;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Walking;

namespace CodeCompass.Semantics;

/// <summary>
/// Precise C/C++ code intelligence via clang (libclang through ClangSharp). It parses
/// each translation unit once, keying declarations by USR (clang's stable, cross-TU
/// symbol identity) so references resolve to the actual symbol - matches in comments
/// and strings are never counted. Uses compile_commands.json when present (root or
/// root/build) for accurate include paths/defines; otherwise best-effort default flags.
///
/// Built once, lazily, and cached. Rebuild by creating a new instance.
/// </summary>
public sealed class ClangCppAnalyzer
{
    private static readonly HashSet<string> SourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx", ".c++" };

    private readonly string _root;
    private readonly object _gate = new();
    private bool _built;

    private readonly Dictionary<string, HashSet<string>> _usrsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Loc>> _defsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Loc>> _refsByUsr = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _lineCache = new(StringComparer.Ordinal);
    private Dictionary<string, string[]>? _compileArgs;

    private readonly record struct Loc(string Rel, int Line, int Column);

    public ClangCppAnalyzer(string root) => _root = Path.GetFullPath(root);

    public IReadOnlyList<SemanticLocation> FindDefinitions(string name)
    {
        EnsureBuilt();
        var result = new List<SemanticLocation>();
        if (_defsByName.TryGetValue(name, out var locs))
        {
            var seen = new HashSet<Loc>();
            foreach (var l in locs)
                if (seen.Add(l)) result.Add(ToSemantic(l));
        }
        return result;
    }

    public IReadOnlyList<SemanticLocation> FindReferences(string name, int max = 200)
    {
        EnsureBuilt();
        var result = new List<SemanticLocation>();
        if (!_usrsByName.TryGetValue(name, out var usrs)) return result;

        var seen = new HashSet<Loc>();
        foreach (var usr in usrs)
        {
            if (!_refsByUsr.TryGetValue(usr, out var locs)) continue;
            foreach (var l in locs)
            {
                if (seen.Add(l))
                {
                    result.Add(ToSemantic(l));
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
            foreach (var file in walker.Walk(_root))
            {
                if (!SourceExtensions.Contains(Path.GetExtension(file.RelativePath))) continue;
                try { ParseFile(index, file.FullPath); }
                catch { /* skip files clang can't handle */ }
            }
        }
    }

    private void ParseFile(CXIndex index, string fullPath)
    {
        var args = GetArgs(fullPath);
        var error = CXTranslationUnit.TryParse(index, fullPath, args,
            ReadOnlySpan<CXUnsavedFile>.Empty,
            CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord,
            out CXTranslationUnit tu);
        if (error != CXErrorCode.CXError_Success) return;

        var translationUnit = TranslationUnit.GetOrCreate(tu);
        Walk(translationUnit.TranslationUnitDecl);
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

        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return false; // skip system headers

        var rel = Path.GetRelativePath(_root, full).Replace('\\', '/');
        loc = new Loc(rel, (int)line, (int)column);
        return true;
    }

    private string[] GetArgs(string fullPath)
    {
        if (_compileArgs is not null && _compileArgs.TryGetValue(Path.GetFullPath(fullPath), out var a))
            return a;
        var dir = Path.GetDirectoryName(fullPath) ?? _root;
        return new[] { "-std=c++17", "-I" + _root, "-I" + dir };
    }

    private void LoadCompileCommands()
    {
        string? path = null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(_root, "compile_commands.json"),
                     Path.Combine(_root, "build", "compile_commands.json"),
                 })
        {
            if (File.Exists(candidate)) { path = candidate; break; }
        }
        if (path is null) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
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

                map[full] = CleanArgs(tokens, fileName);
            }
            _compileArgs = map;
        }
        catch { /* malformed DB: fall back to defaults */ }
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

    private SemanticLocation ToSemantic(Loc loc)
    {
        var text = "";
        if (!_lineCache.TryGetValue(loc.Rel, out var lines))
        {
            try { lines = File.ReadAllLines(Path.Combine(_root, loc.Rel.Replace('/', Path.DirectorySeparatorChar))); }
            catch { lines = Array.Empty<string>(); }
            _lineCache[loc.Rel] = lines;
        }
        if (loc.Line >= 1 && loc.Line <= lines.Length)
            text = lines[loc.Line - 1].Trim();

        return new SemanticLocation(loc.Rel, loc.Line, loc.Column, text);
    }
}
