namespace CodeCompass.Semantics;

/// <summary>Which file types have a semantic analyzer (so lexical fallback can skip them).</summary>
public static class SemanticCoverage
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",                                   // Roslyn
        ".c", ".cc", ".cpp", ".cxx", ".c++",     // clang (C/C++)
        ".h", ".hpp", ".hh", ".hxx",
    };

    public static bool IsCovered(string path) => Extensions.Contains(Path.GetExtension(path));
}
