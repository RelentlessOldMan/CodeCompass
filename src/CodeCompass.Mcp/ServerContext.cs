using CodeCompass.Core.Indexing;
using CodeCompass.Core.Symbols;

namespace CodeCompass.Mcp;

/// <summary>
/// Holds the single repository this server instance serves, plus its in-memory
/// indexes. The MCP server is launched per workspace, so the root is fixed at startup
/// and tools never need to pass paths around. Indexes load from the on-disk cache and
/// are built on first use if absent.
/// </summary>
public static class ServerContext
{
    private static readonly object Gate = new();
    private static TrigramIndex? _text;
    private static SymbolIndex? _symbols;

    public static string Root { get; private set; } = "";

    public static void Init(string root) => Root = Path.GetFullPath(root);

    /// <summary>Returns the loaded indexes, building them once if the cache is empty.</summary>
    public static (TrigramIndex Text, SymbolIndex Symbols) Get()
    {
        lock (Gate)
        {
            if (_text is null || _symbols is null)
            {
                if (RepositoryIndexer.TryLoad(Root, out var text, out var symbols))
                {
                    _text = text;
                    _symbols = symbols;
                }
                else
                {
                    var built = RepositoryIndexer.Build(Root);
                    _text = built.Text;
                    _symbols = built.Symbols;
                }
            }
            return (_text, _symbols);
        }
    }

    public static IndexStats Rebuild()
    {
        lock (Gate)
        {
            var built = RepositoryIndexer.Build(Root);
            _text = built.Text;
            _symbols = built.Symbols;
            return built.Stats;
        }
    }
}
