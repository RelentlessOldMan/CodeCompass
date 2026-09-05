using CodeCompass.Core.Ignore;

namespace CodeCompass.Core.Changes;

/// <summary>
/// Watches a repository tree and, after a quiet debounce window, invokes a flush
/// callback (typically an incremental reindex). Events under ignored directories or for
/// ignored file types are dropped, so build-output churn and a big source-control sync
/// alike collapse into a single reconcile rather than a storm of reindexes. A watcher
/// buffer overflow triggers a flush too, since the incremental update re-walks and
/// reconciles regardless of which individual events were missed.
/// </summary>
public sealed class RepositoryWatcher : IDisposable
{
    private readonly string _root;
    private readonly Action _onFlush;
    private readonly int _debounceMs;
    private readonly IgnoreRules _ignore;
    private readonly FileSystemWatcher _fsw;
    private readonly object _timerGate = new();
    private readonly object _flushGate = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public RepositoryWatcher(string root, Action onFlush, int debounceMs = 1000, IgnoreRules? ignore = null)
    {
        _root = Path.GetFullPath(root);
        _onFlush = onFlush;
        _debounceMs = debounceMs;
        _ignore = ignore ?? new IgnoreRules();

        _fsw = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        _fsw.Changed += OnEvent;
        _fsw.Created += OnEvent;
        _fsw.Deleted += OnEvent;
        _fsw.Renamed += OnRenamed;
        _fsw.Error += OnError;
    }

    public void Start() => _fsw.EnableRaisingEvents = true;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (ShouldReact(e.FullPath)) Schedule();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldReact(e.FullPath) || ShouldReact(e.OldFullPath)) Schedule();
    }

    private void OnError(object sender, ErrorEventArgs e) => Schedule(); // overflow: reconcile fully

    private bool ShouldReact(string fullPath)
    {
        string rel;
        try { rel = Path.GetRelativePath(_root, fullPath); }
        catch { return true; }
        if (rel.StartsWith("..", StringComparison.Ordinal)) return false;

        // Any segment naming an ignored directory (including the leaf, e.g. the "bin"
        // directory's own create event) means we don't care about this change.
        var parts = rel.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
            if (_ignore.IsIgnoredDirectory(part))
                return false;

        var name = parts.Length > 0 ? parts[^1] : "";
        if (name.Contains('.') && _ignore.IsIgnoredFile(name, 0)) return false;
        return true;
    }

    private void Schedule()
    {
        lock (_timerGate)
        {
            if (_disposed) return;
            if (_timer is null)
                _timer = new System.Threading.Timer(_ => Flush(), null, _debounceMs, Timeout.Infinite);
            else
                _timer.Change(_debounceMs, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        // Serialize flushes so a slow reindex can't overlap the next one.
        lock (_flushGate)
        {
            if (_disposed) return;
            try { _onFlush(); }
            catch { /* keep watching even if one reindex fails */ }
        }
    }

    public void Dispose()
    {
        lock (_timerGate) { _disposed = true; }
        try { _fsw.EnableRaisingEvents = false; } catch { }
        _fsw.Dispose();
        _timer?.Dispose();
    }
}
