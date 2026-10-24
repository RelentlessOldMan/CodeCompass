namespace CodeCompass.Core.Ignore;

/// <summary>
/// One folder's ignore files - <c>.gitignore</c>, then <c>.ignore</c> (read second, so it wins, as ripgrep does) - in gitignore
/// syntax: <c>#</c> comments, <c>!</c> re-includes, a trailing <c>/</c> matches folders only, a slash anywhere else anchors the
/// pattern to this folder (otherwise it matches a name at any depth), <c>*</c> <c>?</c> <c>[...]</c> (with POSIX classes
/// like <c>[:digit:]</c>) stop at a slash and <c>**</c> crosses them. The last pattern that matches decides. Matched
/// case-insensitively where the file system is (Windows, macOS), as git does there. Not read: a global excludes file or <c>.git/info/exclude</c> - they aren't the repo's, so two
/// machines would index the same tree differently.
/// </summary>
public sealed class IgnoreFile
{
    /// <summary>The file names read, in order: a later file's patterns win.</summary>
    public static readonly string[] Names = { ".gitignore", ".ignore" };

    // Larger than any real ignore file: one bigger is not an ignore file anyone wrote, and isn't read.
    internal const long MaxBytes = 1024 * 1024;

    // Exactly these names, as git reads them (".Gitignore" isn't one): the walk and a targeted update must agree on which
    // files are ignore files, and the update finds them by name in the index's ledger.
    public static bool IsIgnoreFileName(ReadOnlySpan<char> name) =>
        name.SequenceEqual(".gitignore") || name.SequenceEqual(".ignore");

    private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private readonly record struct Pattern(string Glob, bool Negate, bool DirOnly, bool Anchored);

    private readonly Pattern[] _patterns;

    /// <summary>The folder the files are in, repo-relative and '/'-separated ("" for the root).</summary>
    public string BaseRel { get; }

    public bool IsEmpty => _patterns.Length == 0;

    private IgnoreFile(string baseRel, Pattern[] patterns) { BaseRel = baseRel; _patterns = patterns; }

    /// <summary>Parse the folder's ignore files' texts, in <see cref="Names"/> order.</summary>
    public static IgnoreFile Parse(string baseRel, IEnumerable<string> texts)
    {
        var patterns = new List<Pattern>();
        foreach (var text in texts)
            foreach (var raw in text.Split('\n'))
                if (TryParseLine(raw, out var p)) patterns.Add(p);
        return new IgnoreFile(baseRel, patterns.ToArray());
    }

    private static bool TryParseLine(string raw, out Pattern pattern)
    {
        pattern = default;
        var line = raw.AsSpan().TrimEnd('\r');
        if (line.IsEmpty || line[0] == '#') return false;
        // Trailing spaces go, unless escaped with a backslash.
        int end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && !(end >= 2 && line[end - 2] == '\\')) end--;
        line = line[..end];
        bool negate = false;
        if (line.Length > 0 && line[0] == '!') { negate = true; line = line[1..]; }
        bool dirOnly = false;
        if (line.Length > 0 && line[^1] == '/') { dirOnly = true; line = line[..^1]; }
        bool anchored = line.Contains('/');
        if (line.Length > 0 && line[0] == '/') line = line[1..];
        if (line.IsEmpty) return false;
        pattern = new Pattern(line.ToString(), negate, dirOnly, anchored);
        return true;
    }

    /// <summary>A repo-relative path inside <see cref="BaseRel"/>, made relative to it.</summary>
    public ReadOnlySpan<char> RelFromBase(string rel) => BaseRel.Length == 0 ? rel : rel.AsSpan(BaseRel.Length + 1);

    /// <summary>What the last matching pattern says of a path (relative to <see cref="BaseRel"/>): true ignored, false
    /// re-included, null when no pattern matches.</summary>
    public bool? Match(ReadOnlySpan<char> relFromBase, bool isDir)
    {
        var name = relFromBase[(relFromBase.LastIndexOf('/') + 1)..];
        for (int i = _patterns.Length - 1; i >= 0; i--)
        {
            var p = _patterns[i];
            if (p.DirOnly && !isDir) continue;
            if (WildMatch(p.Glob, p.Anchored ? relFromBase : name) == Matched) return !p.Negate;
        }
        return null;
    }

    // ---- git's wildmatch (wildmatch.c), with WM_PATHNAME: '*' '?' '[...]' never match a slash; "**" between slashes does ----

    private const int Matched = 0, NoMatch = 1, AbortAll = -1, AbortToStarStar = -2;

    private static char At(ReadOnlySpan<char> s, int i) => i < s.Length ? s[i] : '\0';

    private static char Fold(char c) => IgnoreCase ? char.ToLowerInvariant(c) : c;

    // Does a POSIX character class (its name, between "[:" and ":]") hold c? Null for a name git doesn't know. ASCII, as git's
    // ctype is. Given the folded character, as git is: where matching folds case "lower" takes 'A' too, and "upper" takes
    // the folded 'a' (git's WM_CASEFOLD).
    private static bool? ClassMatches(ReadOnlySpan<char> name, char c) => name switch
    {
        "alnum" => char.IsAsciiLetterOrDigit(c),
        "alpha" => char.IsAsciiLetter(c),
        "blank" => c is ' ' or '\t',
        "cntrl" => c < 0x20 || c == 0x7f,
        "digit" => char.IsAsciiDigit(c),
        "graph" => c > 0x20 && c < 0x7f,
        "lower" => char.IsAsciiLetterLower(c),
        "print" => c >= 0x20 && c < 0x7f,
        "punct" => c > 0x20 && c < 0x7f && !char.IsAsciiLetterOrDigit(c),
        "space" => c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f',
        "upper" => char.IsAsciiLetterUpper(c) || (IgnoreCase && char.IsAsciiLetterLower(c)),
        "xdigit" => char.IsAsciiHexDigit(c),
        _ => null,
    };

    private static int WildMatch(ReadOnlySpan<char> p, ReadOnlySpan<char> text)
    {
        int pi = 0, ti = 0;
        for (; pi < p.Length; pi++, ti++)
        {
            char pc = p[pi];
            char tc = At(text, ti);
            if (tc == '\0' && pc != '*') return AbortAll;
            switch (pc)
            {
                case '\\':
                    pc = At(p, ++pi); // a literal: the next character
                    if (Fold(tc) != Fold(pc)) return NoMatch;
                    continue;
                default:
                    if (Fold(tc) != Fold(pc)) return NoMatch;
                    continue;
                case '?':
                    if (tc == '/') return NoMatch;
                    continue;
                case '*':
                {
                    bool matchSlash = false;
                    if (At(p, ++pi) == '*')
                    {
                        int prev = pi - 2;
                        while (At(p, ++pi) == '*') { }
                        if ((prev < 0 || p[prev] == '/') && (At(p, pi) == '\0' || At(p, pi) == '/'))
                        {
                            // "**/" may match no folder at all: try the rest of the pattern here first.
                            if (At(p, pi) == '/' && WildMatch(p[(pi + 1)..], text[ti..]) == Matched) return Matched;
                            matchSlash = true;
                        }
                    }
                    if (pi >= p.Length)
                    {
                        // A trailing "**" matches everything; a trailing "*" only what has no slash left.
                        if (!matchSlash && text[ti..].Contains('/')) return AbortToStarStar;
                        return Matched;
                    }
                    if (!matchSlash && p[pi] == '/')
                    {
                        // One '*' then a slash: it matches the rest of this path segment.
                        int slash = text[ti..].IndexOf('/');
                        if (slash < 0) return AbortAll;
                        ti += slash;
                        break; // the loop steps over the slash in both
                    }
                    for (; ; ti++)
                    {
                        tc = At(text, ti);
                        if (tc == '\0') break;
                        int r = WildMatch(p[pi..], text[ti..]);
                        if (r != NoMatch)
                        {
                            if (!matchSlash || r != AbortToStarStar) return r;
                        }
                        else if (!matchSlash && tc == '/') return AbortToStarStar;
                    }
                    return AbortAll;
                }
                case '[':
                {
                    pc = At(p, ++pi);
                    bool negated = pc == '!' || pc == '^';
                    if (negated) pc = At(p, ++pi);
                    char prevC = '\0';
                    bool matched = false;
                    char ft = Fold(tc);
                    do
                    {
                        if (pc == '\0') return AbortAll;
                        if (pc == '\\')
                        {
                            pc = At(p, ++pi);
                            if (pc == '\0') return AbortAll;
                            if (ft == Fold(pc)) matched = true;
                        }
                        else if (pc == '[' && At(p, pi + 1) == ':')
                        {
                            // A POSIX class, "[:name:]" - up to the first ']', as git reads it.
                            int start = pi + 2, close = start;
                            while (At(p, close) is not '\0' and not ']') close++;
                            if (close >= p.Length) return AbortAll;
                            if (close - 1 < start || p[close - 1] != ':')
                            {
                                // Not a class after all: a plain '['.
                                if (tc == '[') matched = true;
                            }
                            else
                            {
                                switch (ClassMatches(p[start..(close - 1)], ft))
                                {
                                    case null: return AbortAll; // an unknown class
                                    case true: matched = true; break;
                                }
                                pi = close;
                                pc = '\0'; // so a '-' after it can't start a range from it
                            }
                        }
                        else if (pc == '-' && prevC != '\0' && At(p, pi + 1) != '\0' && At(p, pi + 1) != ']')
                        {
                            pc = At(p, ++pi);
                            if (pc == '\\')
                            {
                                pc = At(p, ++pi);
                                if (pc == '\0') return AbortAll;
                            }
                            if ((tc >= prevC && tc <= pc) || (IgnoreCase && ft >= Fold(prevC) && ft <= Fold(pc))) matched = true;
                            pc = '\0'; // so the next character can't start a range from this one
                        }
                        else if (ft == Fold(pc)) matched = true;
                        prevC = pc;
                        pc = At(p, ++pi);
                    } while (pc != ']');
                    if (matched == negated || tc == '/') return NoMatch;
                    continue;
                }
            }
        }
        return ti >= text.Length ? Matched : NoMatch;
    }
}

/// <summary>The ignore files that apply inside one folder: its own and every one above it, deepest first. Immutable, so a
/// parallel walk shares it between the folder's children.</summary>
public sealed class IgnoreScope
{
    /// <summary>No ignore files.</summary>
    public static readonly IgnoreScope None = new(null, null);

    private readonly IgnoreScope? _parent;
    private readonly IgnoreFile? _file;

    public bool IsEmpty { get; }

    private IgnoreScope(IgnoreScope? parent, IgnoreFile? file)
    {
        _parent = parent;
        _file = file;
        IsEmpty = file is null && (parent?.IsEmpty ?? true);
    }

    /// <summary>The scope of a folder inside this one, with that folder's own ignore files (null or empty: none).</summary>
    public IgnoreScope With(IgnoreFile? file) => file is null || file.IsEmpty ? this : new IgnoreScope(this, file);

    /// <summary>Do the ignore files ignore an entry directly in this scope's folder (its repo-relative path)? The deepest file
    /// with a pattern that matches decides.</summary>
    public bool IsIgnored(string entryRel, bool isDir)
    {
        for (var s = this; s is not null; s = s._parent)
            if (s._file is { } f && f.Match(f.RelFromBase(entryRel), isDir) is bool ignored) return ignored;
        return false;
    }
}

/// <summary>
/// Judges any repo-relative path as the walk would, without walking: a path is ignored when a folder above it is (git never
/// looks inside one, so nothing under it can be re-included) or when it is itself. Each folder's ignore files are read from
/// disk once, when a path under it is first judged, and only for the folders <c>exists</c> says may have one. Not thread-safe:
/// one per batch or prune.
/// </summary>
public sealed class IgnoreFileSet
{
    private readonly string _root;
    private readonly IgnoreRules _rules;
    private readonly Func<string, bool> _exists; // repo-relative ignore file path -> may it exist?
    private readonly Dictionary<string, IgnoreScope?> _scopes = new(StringComparer.Ordinal); // null: the folder is ignored

    private IgnoreFileSet(string root, IgnoreRules rules, Func<string, bool> exists)
    {
        _root = root;
        _rules = rules;
        _exists = exists;
    }

    /// <summary>A set whose ignore files are among <paramref name="relPaths"/> (an index's paths: the walk indexes every
    /// ignore file in a folder it enters).</summary>
    public static IgnoreFileSet ForSnapshot(string root, IgnoreRules rules, IEnumerable<string> relPaths)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        if (rules.UseIgnoreFiles)
            foreach (var p in relPaths)
                if (IgnoreFile.IsIgnoreFileName(p.AsSpan(p.LastIndexOf('/') + 1))) files.Add(p);
        return new IgnoreFileSet(root, rules, files.Contains);
    }

    /// <summary>A set asking <paramref name="exists"/> whether a folder may have an ignore file (cheaper than listing them all).</summary>
    public static IgnoreFileSet Lazy(string root, IgnoreRules rules, Func<string, bool> exists) =>
        new(root, rules, rules.UseIgnoreFiles ? exists : _ => false);

    /// <summary>The scope a walk of folder <paramref name="dirRel"/> starts in - the ignore files above it and its own - or
    /// null when it's ignored.</summary>
    public IgnoreScope? ScopeOf(string dirRel)
    {
        if (_scopes.TryGetValue(dirRel, out var known)) return known;
        IgnoreScope? scope;
        if (dirRel.Length == 0) scope = IgnoreScope.None.With(Read(""));
        else
        {
            int cut = dirRel.LastIndexOf('/');
            var parent = ScopeOf(cut < 0 ? "" : dirRel[..cut]);
            scope = parent is null || IsIgnoredIn(parent, dirRel, isDir: true) ? null : parent.With(Read(dirRel));
        }
        _scopes[dirRel] = scope;
        return scope;
    }

    /// <summary>Would the walk leave this repo-relative path out because of an ignore file?</summary>
    public bool IsIgnored(string rel, bool isDir)
    {
        int cut = rel.LastIndexOf('/');
        var scope = ScopeOf(cut < 0 ? "" : rel[..cut]);
        return scope is null || IsIgnoredIn(scope, rel, isDir);
    }

    // The walk's rule for one entry of a folder: keepDirs folders and the ignore files themselves are never left out by one.
    internal static bool IsIgnoredIn(IgnoreScope scope, string rel, bool isDir, IgnoreRules rules)
    {
        if (scope.IsEmpty) return false;
        var name = rel.AsSpan(rel.LastIndexOf('/') + 1);
        if (isDir ? rules.IsKeptDirectory(name.ToString()) : IgnoreFile.IsIgnoreFileName(name)) return false;
        return scope.IsIgnored(rel, isDir);
    }

    private bool IsIgnoredIn(IgnoreScope scope, string rel, bool isDir) => IsIgnoredIn(scope, rel, isDir, _rules);

    private IgnoreFile? Read(string dirRel)
    {
        List<string>? texts = null;
        foreach (var name in IgnoreFile.Names)
        {
            var rel = dirRel.Length == 0 ? name : dirRel + "/" + name;
            if (!_exists(rel)) continue;
            var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (Walking.FileWalker.IsLink(new FileInfo(full))) continue; // never followed, as the walk doesn't (FileWalker.ScopeIn)
            if (ReadText(full) is { } t) (texts ??= new()).Add(t);
        }
        return texts is null ? null : IgnoreFile.Parse(dirRel, texts);
    }

    /// <summary>An ignore file's text, or null if it's gone, unreadable or too big to be one.</summary>
    internal static string? ReadText(string fullPath)
    {
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > IgnoreFile.MaxBytes) return null;
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
