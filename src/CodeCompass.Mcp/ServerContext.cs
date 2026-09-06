using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Semantics;

namespace CodeCompass.Mcp;

/// <summary>
/// Holds the single repository this server instance serves, plus its in-memory symbol
/// index, change snapshot, the memory-mapped segmented text index, and the (lazily built)
/// semantic analyzers. Launched per workspace, so the root is fixed at startup.
/// </summary>
public static class ServerContext
{
    private static readonly object Gate = new();
    private static SegmentedIndex? _text;
    private static SymbolIndex? _symbols;
    private static Dictionary<string, FileState>? _snapshot;
    private static RoslynCSharpAnalyzer? _csharp;
    private static ClangCppAnalyzer? _cpp;
    private static RepositoryWatcher? _watcher;

    public static string Root { get; private set; } = "";

    public static void Init(string root)
    {
        lock (Gate)
        {
            Root = Path.GetFullPath(root);
            _text?.Dispose();
            _text = null;
            _symbols = null;
            _snapshot = null;
            _csharp = null;
            _cpp = null;
        }
    }

    public static (SegmentedIndex Text, SymbolIndex Symbols) Get()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return (_text!, _symbols!);
        }
    }

    public static RoslynCSharpAnalyzer CSharp
    {
        get { lock (Gate) { return _csharp ??= new RoslynCSharpAnalyzer(Root); } }
    }

    public static ClangCppAnalyzer Cpp
    {
        get { lock (Gate) { return _cpp ??= new ClangCppAnalyzer(Root); } }
    }

    public static IndexStats Rebuild()
    {
        lock (Gate)
        {
            _text?.Dispose(); // release mmaps before rebuilding
            _text = null;
            var built = RepositoryIndexer.Build(Root);
            _text = built.Text;
            _symbols = built.Symbols;
            _snapshot = LoadSnapshot();
            _csharp = null;
            _cpp = null;
            return built.Stats;
        }
    }

    /// <summary>Start watching the workspace so the index stays fresh as files change.</summary>
    public static void EnableLiveIndex(int debounceMs = 1000)
    {
        lock (Gate)
        {
            if (_watcher is not null) return;
            _watcher = new RepositoryWatcher(Root, OnChanges, debounceMs);
            _watcher.Start();
        }
    }

    private static void OnChanges(ChangeBatch batch)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (batch.FullReconcile)
            {
                _text!.Dispose();
                _text = null;
                var built = RepositoryIndexer.Build(Root);
                _text = built.Text;
                _symbols = built.Symbols;
                _snapshot = LoadSnapshot();
            }
            else
            {
                RepositoryIndexer.ApplyChanges(_text!, _symbols!, _snapshot!, Root, batch.ChangedFullPaths);
                RepositoryIndexer.Persist(Root, _text!, _symbols!, _snapshot!);
            }
            _csharp = null; // semantic analyzers rebuild lazily against the new state
            _cpp = null;
        }
    }

    // Caller must hold Gate.
    private static void EnsureLoaded()
    {
        if (_text is not null && _symbols is not null && _snapshot is not null) return;

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
        _snapshot = LoadSnapshot();
    }

    private static Dictionary<string, FileState> LoadSnapshot()
    {
        var path = IndexStore.SnapshotPath(Root);
        if (File.Exists(path))
        {
            try
            {
                using var fs = File.OpenRead(path);
                return SnapshotStore.Load(fs);
            }
            catch { /* fall through to empty */ }
        }
        return new Dictionary<string, FileState>(StringComparer.Ordinal);
    }
}
