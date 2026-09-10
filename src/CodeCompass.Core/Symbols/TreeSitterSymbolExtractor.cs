using CodeCompass.Core.Config;
using TreeSitter;

namespace CodeCompass.Core.Symbols;

/// <summary>
/// Extracts definitions from source text using tree-sitter. Grammars and their
/// compiled queries are loaded lazily and cached per language; a grammar/query that
/// fails to load is remembered as failed so that language quietly falls back to
/// lexical-only rather than throwing on every file.
/// </summary>
public sealed class TreeSitterSymbolExtractor : IDisposable
{
    private readonly Dictionary<string, (Language Language, Query Query)> _cache = new();
    private readonly HashSet<string> _failed = new();
    private readonly int _maxChars = CodeCompassConfig.MaxSymbolChars();

    public IReadOnlyList<Symbol> Extract(string relativePath, string text)
    {
        var def = LanguageRegistry.ForPath(relativePath);
        if (def is null) return Array.Empty<Symbol>();

        // Guard: tree-sitter parse cost is ~O(n^2) on pathological content (e.g. huge machine-
        // generated headers), which can hang the whole index. Skip symbol extraction for very
        // large files - they yield almost no useful symbols and are still fully trigram-indexed
        // (text search works). Tunable via CODECOMPASS_MAX_SYMBOL_MB (default 1 MB).
        if (text.Length > _maxChars) return Array.Empty<Symbol>();

        var loaded = GetOrLoad(def);
        if (loaded is null) return Array.Empty<Symbol>();

        var (language, query) = loaded.Value;

        using var parser = new Parser(language);
        using var tree = parser.Parse(text);
        if (tree is null) return Array.Empty<Symbol>();

        var results = new List<Symbol>();
        foreach (var capture in query.Execute(tree.RootNode).Captures)
        {
            var node = capture.Node;
            results.Add(new Symbol(
                node.Text,
                MapKind(capture.Name),
                relativePath,
                node.StartPosition.Row + 1,
                node.StartPosition.Column + 1));
        }
        return results;
    }

    private (Language, Query)? GetOrLoad(LanguageDefinition def)
    {
        if (_cache.TryGetValue(def.Key, out var cached)) return cached;
        if (_failed.Contains(def.Key)) return null;

        try
        {
            var language = new Language(def.NativeLibrary, def.NativeFunction);
            var query = new Query(language, def.QuerySource);
            _cache[def.Key] = (language, query);
            return (language, query);
        }
        catch
        {
            _failed.Add(def.Key);
            return null;
        }
    }

    private static SymbolKind MapKind(string captureName) => captureName switch
    {
        "class" => SymbolKind.Class,
        "interface" => SymbolKind.Interface,
        "struct" => SymbolKind.Struct,
        "enum" => SymbolKind.Enum,
        "record" => SymbolKind.Record,
        "method" => SymbolKind.Method,
        "function" => SymbolKind.Function,
        "property" => SymbolKind.Property,
        "field" => SymbolKind.Field,
        "namespace" => SymbolKind.Namespace,
        "trait" => SymbolKind.Trait,
        "type" => SymbolKind.TypeAlias,
        _ => SymbolKind.Other,
    };

    public void Dispose()
    {
        foreach (var (language, query) in _cache.Values)
        {
            query.Dispose();
            language.Dispose();
        }
        _cache.Clear();
    }
}
