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
    private FileSystemWatcher _fsw; // replaced (under _timerGate) if it dies - see OnError

    private readonly object _pendingGate = new();
    // Ordinal: a case-only rename (Foo.cs -> foo.cs) must deliver BOTH spellings, or the index keeps the stale casing.
    // The value: did the path get a created/deleted/renamed event, not only Changed (see Flush)?
    private readonly Dictionary<string, bool> _pending = new(StringComparer.Ordinal);
    // Created or renamed-in paths named like an ignored file type: passed on only if Flush finds a FOLDER there (see ShouldReact).
    private readonly HashSet<string> _ifFolder = new(StringComparer.Ordinal);
    private bool _overflow;

    private readonly object _timerGate = new();
    private readonly object _flushGate = new();
    private readonly int _maxWaitMs;
    private long _firstPendingTick; // 0 = nothing pending since last flush
    private System.Threading.Timer? _timer;
    private volatile bool _disposed;

    public RepositoryWatcher(string root, Action<ChangeBatch> onFlush, int debounceMs = 1000, IgnoreRules? ignore = null)
    {
        _root = Path.GetFullPath(root);
        _onFlush = onFlush;
        _debounceMs = debounceMs;
        _maxWaitMs = Math.Max(debounceMs * 10, 5000); // fire at least this often under a sustained storm
        _ignore = ignore ?? new IgnoreRules();

        _fsw = CreateFsw();
    }

    private FileSystemWatcher CreateFsw()
    {
        var fsw = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024, // the 8 KB default overflows on any sizable sync (forcing full reconciles)
        };
        fsw.Changed += OnChanged;
        fsw.Created += OnEvent;
        fsw.Deleted += OnEvent;
        fsw.Renamed += OnRenamed;
        fsw.Error += OnError;
        return fsw;
    }

    public void Start() => _fsw.EnableRaisingEvents = true;

    /// <summary>How many times a dead watcher was replaced (diagnostics / tests).</summary>
    public int Restarts => Volatile.Read(ref _restarts);
    private int _restarts;

    internal void RaiseErrorForTest(Exception ex) => OnError(this, new ErrorEventArgs(ex));

    /// <summary>The rules events are filtered by - the watched root's own, when the caller passed them.</summary>
    public IgnoreRules Ignore => _ignore;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        bool gone = e.ChangeType == WatcherChangeTypes.Deleted;
        if (ShouldReact(e.FullPath, gone)) Record(e.FullPath, structural: true);
        else if (!gone) RecordIfFolder(e.FullPath);
    }

    // A file's content changed - or a FOLDER's timestamp moved because an entry in it was added, removed or renamed, which
    // raises its own event. Flush drops those folders.
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldReact(e.FullPath, gone: false)) Record(e.FullPath, structural: false);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Record both sides: old path may be a removed file or vanished directory,
        // new path may be an added file or a directory whose subtree must be indexed.
        bool any = false;
        if (ShouldReact(e.OldFullPath, gone: true)) { Record(e.OldFullPath, structural: true); any = true; }
        if (ShouldReact(e.FullPath, gone: false)) { Record(e.FullPath, structural: true); any = true; }
        else RecordIfFolder(e.FullPath);
        if (any) Schedule();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Either way events were lost, so the next flush reconciles the whole tree.
        lock (_pendingGate) _overflow = true;
        Schedule();
        if (e.GetException() is InternalBufferOverflowException) return; // the watcher keeps running after an overflow

        // Anything else (a share hiccup on a UNC root, an invalidated directory handle) leaves the watcher DEAD: it never
        // raises another event while EnableRaisingEvents still reads true, so the index would silently drift from the
        // tree for the rest of the session. Replace it (off the event thread - we're inside the dying watcher's handler).
        System.Threading.Tasks.Task.Run(() =>
        {
            FileSystemWatcher? dead = null;
            try
            {
                lock (_timerGate)
                {
                    if (_disposed) return;
                    var fresh = CreateFsw();
                    fresh.EnableRaisingEvents = true;
                    dead = _fsw;
                    _fsw = fresh;
                    Interlocked.Increment(ref _restarts);
                }
                Diagnostics.Log.For(_root).Warn($"file watcher failed ({e.GetException()?.GetType().Name}: {e.GetException()?.Message}); restarted it and queued a full reconcile");
            }
            catch (Exception ex)
            {
                Diagnostics.Log.For(_root).Warn($"file watcher failed and could not be restarted ({ex.Message}); live updates are OFF until the session restarts - run 'codecompass update' after changes");
            }
            finally
            {
                if (dead is not null) { try { dead.EnableRaisingEvents = false; } catch { } try { dead.Dispose(); } catch { } }
            }
        });
    }

    private void Record(string fullPath, bool structural)
    {
        lock (_pendingGate) _pending[fullPath] = structural || (_pending.TryGetValue(fullPath, out var was) && was);
        Schedule();
    }

    // A created or renamed-in path ShouldReact turned down. If it's an ignored file TYPE's name, it may be a FOLDER named like
    // one (src/Lib renamed to src/Contoso.Lib): creating or moving a folder in raises its own event only, nothing for its
    // files, so dropping it lost the whole folder. Flush checks, off the event thread.
    private void RecordIfFolder(string fullPath)
    {
        string rel;
        try { rel = Path.GetRelativePath(_root, fullPath); }
        catch { return; }
        if (Storage.PathSafety.IsOutsideRepo(rel)) return;
        var parts = rel.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(_ignore.IsIgnoredDirectory)) return;
        lock (_pendingGate) _ifFolder.Add(fullPath);
        Schedule();
    }

    // <paramref name="gone"/>: a delete, or the old side of a rename - the path no longer exists.
    private bool ShouldReact(string fullPath, bool gone)
    {
        string rel;
        try { rel = Path.GetRelativePath(_root, fullPath); }
        catch { return true; }
        if (Storage.PathSafety.IsOutsideRepo(rel)) return false;

        var parts = rel.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
            if (_ignore.IsIgnoredDirectory(part))
                return false;

        var name = parts.Length > 0 ? parts[^1] : "";
        // An ignored file TYPE (.dll, .obj, .png, ...) - judged by name, with no disk access on this thread. Except a path
        // that's gone: it may have been a FOLDER named like one (Contoso.Lib, Foo.Db), and deleting or moving a folder
        // raises its own event only, nothing for its files. The indexer tells the two apart from what it holds, without
        // touching the disk (a build's clean deletes thousands of such files).
        if (!gone && name.Contains('.') && _ignore.IsIgnoredFile(name, 0)) return false;
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

            List<KeyValuePair<string, bool>> pending;
            List<string> ifFolder;
            bool overflow;
            lock (_pendingGate)
            {
                pending = _pending.ToList();
                ifFolder = _ifFolder.ToList();
                overflow = _overflow;
                _pending.Clear();
                _ifFolder.Clear();
                _overflow = false;
            }
            // Drop a folder that only reported Changed: its timestamp moved because an entry in it was added, removed or
            // renamed, and that entry has its own event. Passed on, it re-walked the folder's whole subtree under the
            // index write lock on every atomic save in it (a temp file + rename: Visual Studio, JetBrains, git, Claude
            // Code's Edit/Write). Checked here, off the event thread, so a slow stat can't overflow the watcher's buffer.
            // (Not after an overflow: the batch is then a full reconcile, and its paths go unused.)
            var paths = overflow ? new List<string>()
                                 : pending.Where(p => p.Value || !Directory.Exists(p.Key)).Select(p => p.Key).ToList();
            if (!overflow)
            {
                // One listing per parent folder, not a stat per path: an in-tree build creates thousands of .o files.
                var seen = new HashSet<string>(paths, StringComparer.Ordinal);
                var unseen = ifFolder.Where(p => !seen.Contains(p)).ToList();
                if (unseen.Count > 0) paths.AddRange(Storage.FolderProbe.Folders(unseen));
            }
            if (paths.Count == 0 && !overflow) return;

            try { _onFlush(new ChangeBatch(paths, overflow)); }
            catch { /* keep watching even if one reindex fails */ }
        }
    }

    public void Dispose()
    {
        // Stop new events first, then mark disposed and kill the timer, then wait for any
        // in-flight Flush to finish. Without the final barrier a callback already dispatched to
        // the thread pool could run _onFlush after Dispose returns - and touch state the caller
        // is about to tear down (disposed mmap indexes). Note: Dispose never holds _timerGate and
        // _flushGate at the same time, so it can't deadlock against Flush (which nests the other way).
        // Mark disposed and take the CURRENT watcher under _timerGate first, so a concurrent restart (OnError) can't
        // install a replacement after we've let go of the old one and leak it.
        FileSystemWatcher fsw;
        lock (_timerGate) { _disposed = true; _timer?.Dispose(); _timer = null; fsw = _fsw; }
        try { fsw.EnableRaisingEvents = false; } catch { }
        try { fsw.Dispose(); } catch { }
        lock (_flushGate) { /* drains any Flush currently running; later Flushes see _disposed */ }
    }
}
