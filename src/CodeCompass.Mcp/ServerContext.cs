using CodeCompass.Core.Changes;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using CodeCompass.Core.Walking;
using CodeCompass.Semantics;

namespace CodeCompass.Mcp;

public enum IndexState { NotStarted, Building, Ready, NeedsCliBuild }

/// <summary>
/// Holds the single repository this server serves and its indexes. Never blocks a tool
/// call on a build: a small workspace is indexed in the background (tools report progress
/// until ready); a large one (over the auto-index threshold) is left for the user to build
/// from the terminal, so a huge first index can't stall or time out the MCP call.
/// </summary>
public static class ServerContext
{
    private static readonly object Gate = new();
    private static SegmentedIndex? _text;
    private static SegmentedSymbolIndex? _symbols;
    private static Dictionary<string, FileState>? _snapshot;
    private static RoslynCSharpAnalyzer? _csharp;
    private static ClangCppAnalyzer? _cpp;
    private static RepositoryWatcher? _watcher;

    private static IndexState _state = IndexState.NotStarted;
    private static Task? _buildTask;
    private static int _progressFiles;
    private static long _progressBytes;
    private static long _progressTotalBytes;

    public static string Root { get; private set; } = "";

    private static long AutoIndexLimitBytes()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB");
        long mb = int.TryParse(env, out var v) && v >= 0 ? v : 100;
        return mb * 1024L * 1024;
    }

    public static void Init(string root)
    {
        lock (Gate)
        {
            Root = Path.GetFullPath(root);
            _text?.Dispose();
            _symbols?.Dispose();
            _text = null;
            _symbols = null;
            _snapshot = null;
            _csharp = null;
            _cpp = null;
            _state = IndexState.NotStarted;
            _buildTask = null;
        }
    }

    /// <summary>
    /// Get the indexes if ready; otherwise false with a human-readable status the tool
    /// should return to the agent (still indexing, or needs a CLI build).
    /// </summary>
    public static bool TryGet(out SegmentedIndex text, out SegmentedSymbolIndex symbols, out string status)
    {
        lock (Gate)
        {
            EnsureStarted();
            switch (_state)
            {
                case IndexState.Ready:
                    text = _text!;
                    symbols = _symbols!;
                    status = "";
                    return true;
                case IndexState.Building:
                    text = null!; symbols = null!;
                    status = BuildingMessage();
                    return false;
                default: // NeedsCliBuild
                    text = null!; symbols = null!;
                    status = CliBuildMessage();
                    return false;
            }
        }
    }

    public static string StatusLine()
    {
        lock (Gate)
        {
            EnsureStarted();
            return _state switch
            {
                IndexState.Ready => $"ready - {_text!.DocumentCount:N0} files indexed",
                IndexState.Building => BuildingMessage(),
                _ => CliBuildMessage(),
            };
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

    /// <summary>Force a synchronous full rebuild (the reindex tool).</summary>
    public static IndexStats Rebuild()
    {
        lock (Gate)
        {
            _text?.Dispose();
            _symbols?.Dispose();
            _text = null;
            _symbols = null;
            var built = RepositoryIndexer.Build(Root);
            _text = built.Text;
            _symbols = built.Symbols;
            _snapshot = null;
            _csharp = null;
            _cpp = null;
            _state = IndexState.Ready;
            return built.Stats;
        }
    }

    public static void EnableLiveIndex(int debounceMs = 1000)
    {
        lock (Gate)
        {
            if (_watcher is not null) return;
            _watcher = new RepositoryWatcher(Root, OnChanges, debounceMs);
            _watcher.Start();
        }
    }

    // Caller must hold Gate. Decides how to bring the index up without blocking.
    private static void EnsureStarted()
    {
        if (_state is IndexState.Ready or IndexState.Building) return;

        // Pick up an index built out-of-band (e.g. the user just ran the CLI).
        if (RepositoryIndexer.TryLoad(Root, out var text, out var symbols))
        {
            _text = text;
            _symbols = symbols;
            _state = IndexState.Ready;
            return;
        }

        // No index yet: large workspaces are left for a CLI build (no long, timeout-prone
        // build inside a tool call); small ones index in the background.
        if (ExceedsAutoLimit(out var total))
        {
            _state = IndexState.NeedsCliBuild;
            return;
        }

        _progressTotalBytes = total;
        _progressFiles = 0;
        _progressBytes = 0;
        _state = IndexState.Building;
        _buildTask = Task.Run(() =>
        {
            var built = RepositoryIndexer.Build(Root, (f, b) => { Volatile.Write(ref _progressFiles, f); Interlocked.Exchange(ref _progressBytes, b); });
            lock (Gate)
            {
                _text = built.Text;
                _symbols = built.Symbols;
                _state = IndexState.Ready;
            }
        });
    }

    private static bool ExceedsAutoLimit(out long totalBytes)
    {
        long limit = AutoIndexLimitBytes();
        long sum = 0;
        foreach (var f in new FileWalker(new IgnoreRules()).Walk(Root))
        {
            sum += f.Size;
            if (sum > limit) { totalBytes = sum; return true; }
        }
        totalBytes = sum;
        return false;
    }

    private static string BuildingMessage()
    {
        long total = Interlocked.Read(ref _progressTotalBytes);
        long done = Interlocked.Read(ref _progressBytes);
        int files = Volatile.Read(ref _progressFiles);
        double pct = total > 0 ? 100.0 * done / total : 0;
        return $"CodeCompass is indexing this workspace: {pct:F0}% ({files:N0} files, " +
               $"{done / 1048576.0:F0}/{total / 1048576.0:F0} MB). Try again shortly.";
    }

    private static string CliBuildMessage() =>
        $"This workspace is large and CodeCompass hasn't indexed it yet. Build the index once " +
        $"from a terminal (it shows progress and won't time out): codecompass index \"{Root}\" " +
        $"- then retry. (Raise CODECOMPASS_MAX_AUTO_MB to auto-index larger workspaces.)";

    private static void OnChanges(ChangeBatch batch)
    {
        lock (Gate)
        {
            if (_state != IndexState.Ready) return; // don't build from the watcher; that's on-demand/CLI

            if (batch.FullReconcile)
            {
                _text!.Dispose();
                _symbols!.Dispose();
                _text = null;
                _symbols = null;
                var built = RepositoryIndexer.Build(Root);
                _text = built.Text;
                _symbols = built.Symbols;
                _snapshot = null;
            }
            else
            {
                _snapshot ??= LoadSnapshot();
                RepositoryIndexer.ApplyChanges(_text!, _symbols!, _snapshot, Root, batch.ChangedFullPaths);
                RepositoryIndexer.Persist(Root, _text!, _symbols!, _snapshot);
            }
            _csharp = null;
            _cpp = null;
        }
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
            catch { /* fall through */ }
        }
        return new Dictionary<string, FileState>(StringComparer.Ordinal);
    }
}
