using CodeCompass.Core.Indexing;
using CodeCompass.Core.Symbols;
using CodeCompass.Semantics;

namespace CodeCompass.Mcp;

/// <summary>
/// Holds the single repository this server instance serves, plus its in-memory
/// indexes and the (lazily built) C# semantic analyzer. The MCP server is launched
/// per workspace, so the root is fixed at startup and tools never pass paths around.
/// </summary>
public static class ServerContext
{
    private static readonly object Gate = new();
    private static TrigramIndex? _text;
    private static SymbolIndex? _symbols;
    private static RoslynCSharpAnalyzer? _csharp;
    private static ClangCppAnalyzer? _cpp;

    public static string Root { get; private set; } = "";

    public static void Init(string root)
    {
        lock (Gate)
        {
            Root = Path.GetFullPath(root);
            _text = null;
            _symbols = null;
            _csharp = null;
            _cpp = null;
        }
    }

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

    /// <summary>The C# semantic analyzer, built lazily and cached for the session.</summary>
    public static RoslynCSharpAnalyzer CSharp
    {
        get
        {
            lock (Gate)
            {
                return _csharp ??= new RoslynCSharpAnalyzer(Root);
            }
        }
    }

    /// <summary>The C/C++ semantic analyzer, built lazily and cached for the session.</summary>
    public static ClangCppAnalyzer Cpp
    {
        get
        {
            lock (Gate)
            {
                return _cpp ??= new ClangCppAnalyzer(Root);
            }
        }
    }

    public static IndexStats Rebuild()
    {
        lock (Gate)
        {
            var built = RepositoryIndexer.Build(Root);
            _text = built.Text;
            _symbols = built.Symbols;
            _csharp = null; // force semantic rebuild on next use
            _cpp = null;
            return built.Stats;
        }
    }
}
