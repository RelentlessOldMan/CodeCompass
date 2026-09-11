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

        // Guard: parse cost is linear in size but the constant varies ~70x by content; a large file
        // can take tens of seconds to parse. Skip symbol extraction for very large files - they yield
        // almost no useful symbols and are still fully trigram-indexed (text search works). Tunable
        // via CODECOMPASS_MAX_SYMBOL_MB (default 1 MB).
        if (text.Length > _maxChars) return Array.Empty<Symbol>();

        // Data-blob guard (fixed 1 MB floor, NOT user-adjustable): only engages above 1 MB, i.e. only
        // when the size cap above has been raised past its default. Large machine-generated numeric/
        // hex data arrays (test vectors, data buffers) parse ~25x slower than code yet contain 0
        // symbols - measured on a real 90 GB repo as the sole cause of the "cap-raised" parse crawl.
        // Skipping them by content lets the cap be raised for large *real* code without reintroducing
        // the crawl. Below 1 MB (where essentially all real symbols live) this never runs.
        if (text.Length > DataBlobCheckMinChars && IsLikelyNumericData(text)) return Array.Empty<Symbol>();

        return ExtractCore(def, relativePath, text);
    }

    // Fixed floor for the data-blob check. Matches the default symbol cap, so the check only ever runs
    // on files a raised cap admits. Deliberately not configurable - it's a safety rail, not a knob.
    public const int DataBlobCheckMinChars = 1024 * 1024;

    /// <summary>
    /// Heuristic: is this text an overwhelmingly numeric/hex data blob (a generated array of literals)
    /// rather than code? Signal is the density of letters OUTSIDE numeric-literal context - i.e. the
    /// hex range a-f/A-F and the hex prefix x/X are NOT counted, everything g-z/G-Z (minus x) is. Those
    /// appear constantly in real identifiers and keywords (int, return, struct, get, ...) but never
    /// inside numeric or hex literals, so a giant numeric array has ~0% of them while real code (and
    /// even register #define headers, which carry macro-name letters) has plenty. One cheap pass.
    /// </summary>
    public static bool IsLikelyNumericData(string text)
    {
        long nonHexLetters = 0, nonWhitespace = 0;
        foreach (char c in text)
        {
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') continue;
            nonWhitespace++;
            // Exclude x/X: they're the "0x" hex prefix, i.e. numeric-literal context, not identifiers.
            if (c is not ('x' or 'X') && ((c >= 'g' && c <= 'z') || (c >= 'G' && c <= 'Z'))) nonHexLetters++;
        }
        if (nonWhitespace == 0) return true; // whitespace only -> nothing to extract anyway
        return nonHexLetters * 100 < nonWhitespace * 2; // < 2% non-hex letters -> numeric data blob
    }

    /// <summary>Parse regardless of the size cap. Diagnostic-only: the <c>symstats</c> profiler uses
    /// this to measure the real parse-cost curve and the true size distribution of symbol-bearing
    /// files. The cap in <see cref="Extract"/> is what protects the indexing hot path - do not call
    /// this there.</summary>
    public IReadOnlyList<Symbol> ExtractUncapped(string relativePath, string text)
    {
        var def = LanguageRegistry.ForPath(relativePath);
        if (def is null) return Array.Empty<Symbol>();
        return ExtractCore(def, relativePath, text);
    }

    private IReadOnlyList<Symbol> ExtractCore(LanguageDefinition def, string relativePath, string text)
    {
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
