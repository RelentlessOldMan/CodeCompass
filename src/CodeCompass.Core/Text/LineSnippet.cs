namespace CodeCompass.Core.Text;

/// <summary>
/// The text of a matched line as it is QUOTED back to the agent: bounded in length and scrubbed of characters
/// that can forge output. Repo content is untrusted and lands verbatim in an LLM's context, so:
/// <list type="bullet">
/// <item>A single-line minified/generated/hostile file can make one "line" hundreds of MB; copying it per hit
/// could exhaust memory and flood the context. Lines over <see cref="MaxChars"/> are windowed around the match.</item>
/// <item>An embedded CR, other C0 controls, a Unicode line/paragraph separator or a bidi override can render as a
/// forged extra result or note line ("x\rC:\other.cs:1:1: (Note: ...)"). Each is replaced by one visible
/// placeholder character, so column offsets are unchanged.</item>
/// </list>
/// </summary>
public static class LineSnippet
{
    public const int MaxChars = 300;
    private const int Lead = 120; // context kept before the match when windowing
    private const char Ellipsis = '…';
    private const char Placeholder = '·';

    /// <summary>The display text for <paramref name="line"/>[<paramref name="start"/>..<paramref name="start"/>+
    /// <paramref name="length"/>) containing a match at <paramref name="matchIndex"/> (absolute index into
    /// <paramref name="line"/>, length <paramref name="matchLength"/>), plus <c>offset</c>: the column of the
    /// returned text's first char relative to the line start (0 when the whole line fits). A consumer mapping a
    /// 1-based line column into the returned text uses <c>column - 1 - offset</c>.</summary>
    public static (string Text, int Offset) Make(string line, int start, int length, int matchIndex, int matchLength)
    {
        if (length <= MaxChars) return (Scrub(line.AsSpan(start, length)), 0);

        int rel = matchIndex - start;
        // Keep at least one real character on each side of the match so a whole-word check never mistakes the
        // ellipsis for a boundary.
        int winStart = Math.Max(0, rel - Lead);
        int winEnd = Math.Min(length, Math.Max(winStart + MaxChars, rel + matchLength + 1));
        bool cutLeft = winStart > 0, cutRight = winEnd < length;
        var body = Scrub(line.AsSpan(start + winStart, winEnd - winStart));
        var text = (cutLeft ? Ellipsis.ToString() : "") + body + (cutRight ? Ellipsis.ToString() : "");
        return (text, winStart - (cutLeft ? 1 : 0));
    }

    /// <summary>Whole-line convenience: <see cref="Make(string,int,int,int,int)"/> over all of <paramref name="line"/>.</summary>
    public static (string Text, int Offset) Make(string line, int matchIndex, int matchLength) =>
        Make(line, 0, line.Length, matchIndex, matchLength);

    /// <summary>Bound and scrub a line shown WITHOUT a known match position (a snippet line): keep its head.</summary>
    public static string Display(string line) =>
        line.Length <= MaxChars ? Scrub(line.AsSpan()) : Scrub(line.AsSpan(0, MaxChars)) + Ellipsis;

    private static string Scrub(ReadOnlySpan<char> s)
    {
        int i = 0;
        while (i < s.Length && !IsUnsafe(s[i])) i++;
        if (i == s.Length) return s.ToString();
        var buf = s.ToArray();
        for (; i < buf.Length; i++) if (IsUnsafe(buf[i])) buf[i] = Placeholder;
        return new string(buf);
    }

    // C0 controls except TAB, DEL, C1 NEL, line/paragraph separators, and the bidi embedding/override/isolate
    // controls ("trojan source" reordering).
    private static bool IsUnsafe(char c) =>
        (c < 0x20 && c != 0x09) || c == 0x7F || c == 0x85 || c == 0x2028 || c == 0x2029 ||
        (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069);
}
