namespace CodeCompass.Core.Text;

/// <summary>
/// Decides whether a lexical (whole-word text) hit in a file counts as a CODE reference. A symbol name that
/// appears in a data/doc file (a CSV row, a JSON tag dump, a .md doc, a .log line) or in a build/toolchain
/// artifact committed beside the sources (a .lst disassembly listing, a .bak backup, a preprocessed/object/
/// image file) is NOT a code reference - counting it turns find_references' lexical fallback into noise.
/// Shared by the MCP find_references and the CLI `refs` so both filter identically (firmware trees commit
/// build output next to source, which otherwise dominates the results).
/// </summary>
public static class ReferenceFileFilter
{
    private static readonly HashSet<string> NonCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".json", ".jsonl", ".ndjson", ".yaml", ".yml",
        ".md", ".markdown", ".rst", ".txt", ".log", ".html", ".htm", ".svg", ".map", ".lock",
        // build/toolchain artifacts interleaved with sources in firmware trees
        ".lst", ".bak", ".i", ".s", ".d", ".o", ".obj", ".elf", ".hex", ".bin",
    };

    public static bool IsCodeReference(string path) => !NonCode.Contains(Path.GetExtension(path));
}
