using System.Text;
using System.Text.RegularExpressions;

namespace CodeCompass.Core.SelfTest;

/// <summary>One "path:line:col: text" row of a tool answer (search_code, find_references, find_callees, search_symbols).</summary>
public readonly record struct AnswerHit(string Path, int Line, int Column, string Text);

/// <summary>What a find_references answer is: rows with the footer, the no-references answer, or anything else.</summary>
public enum RefsAnswer { Rows, NoneFound, Other }

/// <summary>One find_definition row: "path:line[-endLine]:col: Kind Name".</summary>
public readonly record struct AnswerDef(string Path, int Line, int EndLine, int Column, string Kind, string Name);

/// <summary>
/// The self-test's oracles: parse a tool's text answer back into rows, and check each row against the file on disk -
/// the answer is right only if the file says the same thing, so nothing here trusts the index.
/// </summary>
public static class AnswerChecks
{
    private static readonly Regex HitRx = new(@"^(.+?):(\d+):(\d+): (.*)$", RegexOptions.Compiled);
    private static readonly Regex DefRx = new(@"^(.+?):(\d+)(?:-(\d+))?:(\d+): (\S+) (\S+)$", RegexOptions.Compiled);
    private static readonly Regex RefsFooterRx = new(@"\((\d+) C# semantic \+ (\d+) name-matched reference\(s\)\)", RegexOptions.Compiled);

    /// <summary>The answer's result rows, up to its first blank line (what follows is a footer, a note or a snippet).</summary>
    public static List<AnswerHit> Hits(string answer)
    {
        var rows = new List<AnswerHit>();
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) break;
            var m = HitRx.Match(line);
            if (m.Success) rows.Add(new AnswerHit(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Value));
        }
        return rows;
    }

    /// <summary>find_definition's rows, up to its first blank line (a single small definition's source follows one).</summary>
    public static List<AnswerDef> Defs(string answer)
    {
        var rows = new List<AnswerDef>();
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) break;
            var m = DefRx.Match(line);
            if (!m.Success) continue;
            int start = int.Parse(m.Groups[2].Value);
            int end = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : start;
            rows.Add(new AnswerDef(m.Groups[1].Value, start, end, int.Parse(m.Groups[4].Value), m.Groups[5].Value, m.Groups[6].Value));
        }
        return rows;
    }

    /// <summary>Did the answer say it was cut off (more results exist than it shows)?</summary>
    public static bool Truncated(string answer) => answer.Contains("MORE EXIST", StringComparison.Ordinal);

    /// <summary>find_references' footer split: (C# semantic, name-matched); null when the answer has none (no hits).</summary>
    public static (int Semantic, int Named)? RefsBreakdown(string answer)
    {
        var m = RefsFooterRx.Match(answer);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    public static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Does <paramref name="text"/> hold <paramref name="word"/> at 1-based <paramref name="column"/>, as a whole word?</summary>
    public static bool WholeWordAt(string text, int column, string word)
    {
        int i = column - 1;
        if (i < 0 || i + word.Length > text.Length) return false;
        if (string.CompareOrdinal(text, i, word, 0, word.Length) != 0) return false;
        bool before = i == 0 || !IsIdentChar(text[i - 1]);
        bool after = i + word.Length == text.Length || !IsIdentChar(text[i + word.Length]);
        return before && after;
    }

    /// <summary>Does <paramref name="text"/> hold <paramref name="word"/> anywhere as a whole word?</summary>
    public static bool HasWholeWord(string text, string word)
    {
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
            if (WholeWordAt(text, i + 1, word)) return true;
        return false;
    }

    /// <summary>Is a find_references row at <paramref name="column"/> of <paramref name="line"/> a reference to
    /// <paramref name="name"/>? The name itself; an attribute's short form ([Obsolete] for ObsoleteAttribute); or, in C#,
    /// a target-typed <c>new()</c>, which constructs the type without naming it.</summary>
    public static bool ReferenceAt(string path, string line, int column, string name, bool isType) =>
        WholeWordAt(line, column, name) ||
        (ShortAttribute(name) is { } shortName && WholeWordAt(line, column, shortName)) ||
        (isType && IsCSharp(path) && WholeWordAt(line, column, "new") && line.AsSpan(column - 1 + 3).TrimStart().StartsWith("("));

    // The members C# calls without naming them (a using's Dispose, a foreach's GetEnumerator, an await's GetAwaiter...):
    // the same list as RoslynCSharpAnalyzer.ImplicitlyCalled, which leaves them out of the scoped search for that reason.
    private static readonly HashSet<string> ImplicitlyCalled = new(StringComparer.Ordinal)
    {
        "GetEnumerator", "GetAsyncEnumerator", "Current", "MoveNext", "MoveNextAsync", "Dispose", "DisposeAsync", "Deconstruct",
        "GetAwaiter", "GetResult", "IsCompleted", "OnCompleted", "UnsafeOnCompleted", "Add", "Length", "Count", "Slice",
        "Substring", "Item", "GetPinnableReference", "Invoke", "Select", "SelectMany", "Where", "Join", "GroupJoin", "OrderBy",
        "OrderByDescending", "ThenBy", "ThenByDescending", "GroupBy", "Cast", "AppendLiteral", "AppendFormatted", "EnterScope",
        "Finalize",
    };

    /// <summary>Can a C# reference to <paramref name="name"/> sit where the code never spells it? Then a row that doesn't
    /// hold the name is unprovable from the text, not wrong.</summary>
    public static bool CanBeImplicit(string path, string name) => IsCSharp(path) && ImplicitlyCalled.Contains(name);

    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>A find_references answer: rows ending in the semantic/by-name footer, the no-references answer, or anything
    /// else (a failure message, an answer cut short).</summary>
    public static RefsAnswer RefsShape(string answer) =>
        Hits(answer).Count > 0 && RefsBreakdown(answer) is not null ? RefsAnswer.Rows
        : answer.StartsWith("No references found", StringComparison.Ordinal) ? RefsAnswer.NoneFound
        : RefsAnswer.Other;

    /// <summary>Did manage_links action=focus take? ("No root matches ... Focus unchanged" is an answer, not an error.)</summary>
    public static bool FocusTook(string answer) =>
        answer.StartsWith("Focused on ", StringComparison.Ordinal) || answer.StartsWith("Focus set to ", StringComparison.Ordinal);

    /// <summary>Does <paramref name="line"/> name <paramref name="name"/> anywhere (so a row that isn't at its column is
    /// at the wrong column, not on the wrong line)?</summary>
    public static bool ReferenceOnLine(string line, string name) =>
        HasWholeWord(line, name) || (ShortAttribute(name) is { } shortName && HasWholeWord(line, shortName));

    private static string? ShortAttribute(string name) =>
        name.Length > 9 && name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^9] : null;

    /// <summary>The identifier that starts at 1-based <paramref name="column"/> of <paramref name="text"/> ("" if none).</summary>
    public static string IdentifierAt(string text, int column)
    {
        int i = column - 1;
        if (i < 0 || i >= text.Length) return "";
        int j = i;
        while (j < text.Length && IsIdentChar(text[j])) j++;
        return text.Substring(i, j - i);
    }
}

/// <summary>
/// Reads repo files' lines for the oracles, each file once. Splits on '\n' only (a lone '\r' doesn't end a line for the
/// index either) and decodes as the index does: UTF-8 unless a byte-order mark says otherwise. Files over
/// <see cref="MaxBytes"/> aren't read - a hit in one is reported as not checked, never as wrong.
/// </summary>
public sealed class RepoLines
{
    public const long MaxBytes = 32L << 20;
    private readonly string _root;
    private readonly Dictionary<string, string[]?> _cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public RepoLines(string root) => _root = root;

    /// <summary>The full path of an answer's path: repo-relative for the project, absolute for a linked root.</summary>
    public string FullPath(string answerPath) =>
        Path.IsPathRooted(answerPath) ? answerPath : Path.Combine(_root, answerPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Line <paramref name="line"/> (1-based) of the file; null if the file is missing, too big, or shorter.</summary>
    public string? Line(string answerPath, int line)
    {
        var lines = Lines(answerPath);
        return lines is not null && line >= 1 && line <= lines.Length ? lines[line - 1] : null;
    }

    /// <summary>Lines <paramref name="from"/>..<paramref name="to"/> (1-based, inclusive) joined; null if not readable.</summary>
    public string? Span(string answerPath, int from, int to)
    {
        var lines = Lines(answerPath);
        if (lines is null || from < 1 || from > lines.Length) return null;
        return string.Join('\n', lines, from - 1, Math.Min(to, lines.Length) - from + 1);
    }

    /// <summary>Is the file too big to check (so a hit in it is "not checked", not "wrong")?</summary>
    public bool TooBig(string answerPath)
    {
        try { return new FileInfo(FullPath(answerPath)).Length > MaxBytes; } catch { return false; }
    }

    private string[]? Lines(string answerPath)
    {
        var full = FullPath(answerPath);
        if (_cache.TryGetValue(full, out var cached)) return cached;
        string[]? lines = null;
        try
        {
            if (new FileInfo(full).Length <= MaxBytes)
            {
                using var reader = new StreamReader(full, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                lines = reader.ReadToEnd().Split('\n');
                for (int i = 0; i < lines.Length; i++) if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
            }
        }
        catch { lines = null; }
        _cache[full] = lines;
        return lines;
    }
}
