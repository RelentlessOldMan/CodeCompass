using System.ComponentModel;
using System.Linq;
using System.Text;
using CodeCompass.Core.Text;
using CodeCompass.Semantics;
using ModelContextProtocol.Server;

namespace CodeCompass.Mcp;

/// <summary>
/// The tools exposed to the agent. Each returns compact, ranked file:line:col results so
/// the agent gets exactly the lines it needs instead of reading whole files. If the index
/// isn't ready yet, a tool returns a short status (still indexing, or how to build it) so
/// the agent can relay progress rather than hang. Deliberately a small surface (5 tools) to
/// keep the per-session token cost low.
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
        => ServerContext.Query((text, _) =>
    {
        // Fetch one extra to detect truncation: if we get maxResults+1 back, there are more than we
        // show, so tell the agent to narrow rather than trust this as the complete set.
        var matches = text.Search(query, maxResults + 1);
        if (matches.Count == 0) return $"No matches for \"{query}\".";

        bool truncated = matches.Count > maxResults;
        var sb = new StringBuilder();
        foreach (var m in matches.Take(maxResults)) sb.AppendLine($"{m.Path}:{m.Line}:{m.Column}: {m.LineText}");
        sb.Append(Footer(Math.Min(matches.Count, maxResults), truncated, "match", "matches"));
        return sb.ToString();
    });

    // Result footer that distinguishes an exact count from a truncated one, so the agent knows
    // whether it has seen everything or must refine the query. `shown` is how many we actually list.
    private static string Footer(int shown, bool truncated, string singular, string plural) =>
        truncated
            ? $"(showing the first {shown} {plural}; MORE EXIST - narrow the query, e.g. add surrounding text or a longer/more specific identifier)"
            : $"({shown} {(shown == 1 ? singular : plural)})";

    [McpServerTool(Name = "find_definition")]
    [Description("Find where a symbol (class, method, function, type, etc.) is defined, by exact name. " +
                 "Returns 'file:line:col: Kind Name'. Use this for go-to-definition instead of searching files.")]
    public static string FindDefinition(
        [Description("Exact symbol name (case-sensitive).")] string name)
        => ServerContext.Query((_, symbols) =>
    {
        var matches = symbols.FindByName(name);
        if (matches.Count == 0) return $"No definition found for \"{name}\".";

        var sb = new StringBuilder();
        foreach (var s in matches) sb.AppendLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.Kind} {s.Name}");
        sb.Append($"({matches.Count} definition(s))");
        return sb.ToString();
    });

    [McpServerTool(Name = "find_references")]
    [Description("Find where a symbol is used across the codebase. For C# (Roslyn) and C/C++ (clang) " +
                 "this is SEMANTIC - it resolves the actual symbol and ignores matches in comments and " +
                 "strings. For other languages it falls back to whole-word lexical matches. " +
                 "Returns ranked 'file:line:col: line'.")]
    public static string FindReferences(
        [Description("Symbol/identifier to find references to (case-sensitive).")] string name,
        [Description("Maximum number of results.")] int maxResults = 100)
        => ServerContext.Query((text, _) =>
    {
        // Collect one past the cap across all sources (C# semantic, C/C++ semantic, then lexical in
        // other files) so truncation is detected by the same overflow probe the other tools use -
        // exact, not a fuzzy threshold. Kind tags let the footer report the shown breakdown.
        int probe = maxResults + 1;
        var hits = new List<(string Line, char Kind)>();
        foreach (var s in ServerContext.CSharp.FindReferences(name, probe))
            hits.Add(($"{s.RelativePath}:{s.Line}:{s.Column}: {s.LineText}", 'c'));
        foreach (var s in ServerContext.Cpp.FindReferences(name, probe))
            hits.Add(($"{s.RelativePath}:{s.Line}:{s.Column}: {s.LineText}", 'p'));

        if (hits.Count <= maxResults)
            foreach (var m in text.Search(name, probe * 5))
            {
                if (SemanticCoverage.IsCovered(m.Path)) continue;             // semantic files handled above
                if (!WordBoundary.IsWholeWord(m.LineText, m.Column - 1, name.Length)) continue;
                hits.Add(($"{m.Path}:{m.Line}:{m.Column}: {m.LineText}", 'l'));
                if (hits.Count > maxResults) break;                          // got the overflow row
            }

        if (hits.Count == 0) return $"No references found for \"{name}\".";

        bool truncated = hits.Count > maxResults;
        var shown = hits.Take(maxResults).ToList();
        var sb = new StringBuilder();
        foreach (var (line, _) in shown) sb.AppendLine(line);
        int cs = shown.Count(h => h.Kind == 'c'), cpp = shown.Count(h => h.Kind == 'p'), lex = shown.Count(h => h.Kind == 'l');
        sb.Append($"({cs} C# + {cpp} C/C++ semantic reference(s); {lex} lexical in other files)");
        if (truncated) sb.Append(" - MORE EXIST, narrow the query or raise the limit");
        return sb.ToString();
    });

    [McpServerTool(Name = "search_symbols")]
    [Description("Search symbol names by case-insensitive substring. " +
                 "Returns 'file:line:col: Kind Name'. Use this to discover related definitions.")]
    public static string SearchSymbols(
        [Description("Substring to match against symbol names (case-insensitive).")] string query,
        [Description("Maximum number of results.")] int maxResults = 50)
        => ServerContext.Query((_, symbols) =>
    {
        var matches = symbols.Find(query, maxResults + 1);
        if (matches.Count == 0) return $"No symbols matching \"{query}\".";

        bool truncated = matches.Count > maxResults;
        var sb = new StringBuilder();
        foreach (var s in matches.Take(maxResults)) sb.AppendLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.Kind} {s.Name}");
        sb.Append(Footer(Math.Min(matches.Count, maxResults), truncated, "symbol", "symbols"));
        return sb.ToString();
    });

    [McpServerTool(Name = "reindex")]
    [Description("Rebuild the CodeCompass index for this workspace from scratch. Also reports index " +
                 "status. Run this after large external changes (e.g. a source-control sync) if results seem stale.")]
    public static string Reindex()
    {
        var s = ServerContext.Rebuild();
        return $"Reindexed {s.Files} files ({s.Bytes / (1024.0 * 1024.0):F1} MB) in {s.Seconds:F2}s; " +
               $"{s.Symbols} symbols.";
    }
}
