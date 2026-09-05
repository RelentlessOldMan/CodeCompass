namespace CodeCompass.Core.Text;

/// <summary>Whole-word matching for lexical reference search (used where semantics aren't available).</summary>
public static class WordBoundary
{
    public static bool IsWholeWord(string line, int start, int length)
    {
        char before = start > 0 ? line[start - 1] : ' ';
        int afterIndex = start + length;
        char after = afterIndex < line.Length ? line[afterIndex] : ' ';
        return !IsIdentifierChar(before) && !IsIdentifierChar(after);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
