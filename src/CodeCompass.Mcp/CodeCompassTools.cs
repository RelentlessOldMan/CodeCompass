using System.ComponentModel;
using System.Text;
using CodeCompass.Core.Text;
using ModelContextProtocol.Server;

namespace CodeCompass.Mcp;

/// <summary>
/// The tools exposed to the agent. Each returns compact, ranked file:line:col results
/// (or symbol locations) so the agent gets exactly the lines it needs instead of
/// reading whole files. This is where the token savings come from.
/// </summary>
[McpServerToolType]
public static class CodeCompassTools
{
    [McpServerTool(Name = "search_code")]
    [Description("Search the indexed codebase for a literal text/substring (case-sensitive). " +
                 "Returns ranked 'file:line:col: matched line' results. Prefer this over grep or " +
                 "reading whole files - it is faster and returns only the relevant lines.")]
    public static string SearchCode(
        [Description("Literal substring to find (case-sensitive).")] string query,
        [Description("Maximum number of results.")] int maxResults = 50)
    {
        var (text, _) = ServerContext.Get();
        var matches = text.Search(query, maxResults);
        if (matches.Count == 0) return $"No matches for \"{query}\".";

        var sb = new StringBuilder();
        foreach (var m in matches)
            sb.AppendLine($"{m.Path}:{m.Line}:{m.Column}: {m.LineText}");
        sb.Append($"({matches.Count} match(es))");
        return sb.ToString();
    }

    [McpServerTool(Name = "find_definition")]
    [Description("Find where a symbol (class, method, function, type, etc.) is defined, by exact name. " +
                 "Returns 'file:line:col: Kind Name'. Use this for go-to-definition instead of searching files.")]
    public static string FindDefinition(
        [Description("Exact symbol name (case-sensitive).")] string name)
    {
        var (_, symbols) = ServerContext.Get();
        var matches = symbols.FindByName(name);
        if (matches.Count == 0) return $"No definition found for \"{name}\".";

        var sb = new StringBuilder();
        foreach (var s in matches)
            sb.AppendLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.Kind} {s.Name}");
        sb.Append($"({matches.Count} definition(s))");
        return sb.ToString();
    }

    [McpServerTool(Name = "find_references")]
    [Description("Find where a symbol is used across the codebase. For C# this is SEMANTIC - " +
                 "it resolves the actual symbol and ignores matches in comments and strings. For other " +
                 "languages it falls back to whole-word lexical matches. Returns ranked 'file:line:col: line'.")]
    public static string FindReferences(
        [Description("Symbol/identifier to find references to (case-sensitive).")] string name,
        [Description("Maximum number of results.")] int maxResults = 100)
    {
        var (text, _) = ServerContext.Get();
        var sb = new StringBuilder();

        // Precise C# references (comments/strings excluded).
        var semantic = ServerContext.CSharp.FindReferences(name, maxResults);
        foreach (var s in semantic)
            sb.AppendLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.LineText}");

        // Lexical whole-word references for non-C# files.
        int lexical = 0;
        foreach (var m in text.Search(name, maxResults * 5))
        {
            if (m.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (!WordBoundary.IsWholeWord(m.LineText, m.Column - 1, name.Length)) continue;
            sb.AppendLine($"{m.Path}:{m.Line}:{m.Column}: {m.LineText}");
            if (++lexical + semantic.Count >= maxResults) break;
        }

        if (semantic.Count == 0 && lexical == 0) return $"No references found for \"{name}\".";
        sb.Append($"({semantic.Count} semantic C# reference(s); {lexical} lexical reference(s) in other files)");
        return sb.ToString();
    }

    [McpServerTool(Name = "search_symbols")]
    [Description("Search symbol names by case-insensitive substring. " +
                 "Returns 'file:line:col: Kind Name'. Use this to discover related definitions.")]
    public static string SearchSymbols(
        [Description("Substring to match against symbol names (case-insensitive).")] string query,
        [Description("Maximum number of results.")] int maxResults = 50)
    {
        var (_, symbols) = ServerContext.Get();
        var matches = symbols.Find(query, maxResults);
        if (matches.Count == 0) return $"No symbols matching \"{query}\".";

        var sb = new StringBuilder();
        foreach (var s in matches)
            sb.AppendLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.Kind} {s.Name}");
        sb.Append($"({matches.Count} symbol(s))");
        return sb.ToString();
    }

    [McpServerTool(Name = "reindex")]
    [Description("Rebuild the CodeCompass index for this workspace from scratch. " +
                 "Run this after large external changes (e.g. a source-control sync) if results seem stale.")]
    public static string Reindex()
    {
        var s = ServerContext.Rebuild();
        return $"Reindexed {s.Files} files ({s.Bytes / (1024.0 * 1024.0):F1} MB) in {s.Seconds:F2}s; " +
               $"{s.Trigrams} trigrams, {s.Symbols} symbols.";
    }
}
