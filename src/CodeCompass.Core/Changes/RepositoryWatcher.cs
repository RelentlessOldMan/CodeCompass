using CodeCompass.Core.Ignore;

namespace CodeCompass.Core.Changes;

/// <summary>A debounced batch of changes: the specific paths that changed, or a request
/// to reconcile the whole tree when events were lost (watcher buffer overflow).</summary>
public sealed record ChangeBatch(IReadOnlyList<string> ChangedFullPaths, bool FullReconcile);

/// <summary>
/// Watches a repository tree and, after a quiet debounce window, hands the accumulated
/// set of changed paths to a callback (typically a targeted incremental reindex). Events
/// under ignored directories or for ignored file types are dropped, so build-output churn
/// and a big source-control sync alike collapse into a single batch. A watcher buffer
/// overflow sets FullReconcile, since individual events were lost and only a full re-walk
/// can be trusted.
/// </summary>
public sealed class RepositoryWatcher : IDisposable
{
    private readonly string _root;
    private readonly Action<ChangeBatch> _onFlush;
    private readonly int _debounceMs;
    private readonly IgnoreRules _ignore;
    private readonly FileSystemWatcher _fsw;

    private readonly object _pendingGate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _overflow;

    private readonly object _timerGate = new();
    private readonly object _flushGate = new();
    private readonly int _maxWaitMs;
    private long _firstPendingTick; // 0 = nothing pending since last flush
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public RepositoryWatcher(string root, Action<ChangeBatch> onFlush, int debounceMs = 1000, IgnoreRules? ignore = null)
    {
        _root = Path.GetFullPath(root);
        _onFlush = onFlush;
        _debounceMs = debounceMs;
        _maxWaitMs = Math.Max(debounceMs * 10, 5000); // fire at least this often under a sustained storm
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
        if (ShouldReact(e.FullPath)) Record(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Record both sides: old path may be a removed file or vanished directory,
        // new path may be an added file or a directory whose subtree must be indexed.
        bool any = false;
        if (ShouldReact(e.OldFullPath)) { Record(e.OldFullPath); any = true; }
        if (ShouldReact(e.FullPath)) { Record(e.FullPath); any = true; }
        if (any) Schedule();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_pendingGate) _overflow = true;
        Schedule();
    }

    private void Record(string fullPath)
    {
        lock (_pendingGate) _pending.Add(fullPath);
        Schedule();
    }

    private bool ShouldReact(string fullPath)
    {
        string rel;
        try { rel = Path.GetRelativePath(_root, fullPath); }
        catch { return true; }
        if (rel.StartsWith("..", StringComparison.Ordinal)) return false;

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
            long now = Environment.TickCount64;
            if (_firstPendingTick == 0) _firstPendingTick = now;
            // Normally reset the quiet window on each event; but if we've been continuously
            // deferring past the max wait (e.g. a long sync), fire now so a storm can't
            // starve the flush and let _pending grow without bound.
            int due = now - _firstPendingTick >= _maxWaitMs ? 0 : _debounceMs;
            if (_timer is null)
                _timer = new System.Threading.Timer(_ => Flush(), null, due, Timeout.Infinite);
            else
                _timer.Change(due, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        lock (_flushGate)
        {
            if (_disposed) return;
            lock (_timerGate) _firstPendingTick = 0; // start a fresh max-wait window

            List<string> paths;
            bool overflow;
            lock (_pendingGate)
            {
                paths = _pending.ToList();
                overflow = _overflow;
                _pending.Clear();
                _overflow = false;
            }
            if (paths.Count == 0 && !overflow) return;

            try { _onFlush(new ChangeBatch(paths, overflow)); }
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
