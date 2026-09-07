using CodeCompass.Core.Changes;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
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
///
/// Concurrency: a <see cref="ReaderWriterLockSlim"/> guards the index. Searches run under a
/// read lock (so the mmap indexes can't be disposed or mutated mid-read); rebuilds and
/// incremental updates take the write lock. Long rebuilds are done off-lock and swapped in
/// under a brief write lock so searches aren't blocked for the whole build. Changes that
/// arrive while a build is in flight are captured and drained when it finishes.
/// </summary>
public static class ServerContext
{
    private static readonly ReaderWriterLockSlim Rw = new(LockRecursionPolicy.NoRecursion);
    private static readonly object AnalyzerGate = new();

    private static SegmentedIndex? _text;
    private static SegmentedSymbolIndex? _symbols;
    private static DiskSnapshot? _snapshot;
    private static RoslynCSharpAnalyzer? _csharp;
    private static ClangCppAnalyzer? _cpp;
    private static RepositoryWatcher? _watcher;

    private static IndexState _state = IndexState.NotStarted;
    private static int _progressFiles;
    private static long _progressBytes;
    private static long _progressTotalBytes;

    // Changes observed while a build is running (state == Building); drained on completion.
    private static readonly List<string> _pendingPaths = new();
    private static bool _pendingReconcile;

    public static string Root { get; private set; } = "";

    private static long AutoIndexLimitBytes()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB");
        long mb = int.TryParse(env, out var v) && v >= 0 ? v : 100;
        return mb * 1024L * 1024;
    }

    public static void Init(string root)
    {
        Rw.EnterWriteLock();
        try
        {
            Root = Path.GetFullPath(root);
            _text?.Dispose();
            _symbols?.Dispose();
            _snapshot?.Dispose();
            _text = null;
            _symbols = null;
            _snapshot = null;
            _csharp = null;
            _cpp = null;
            _state = IndexState.NotStarted;
            _pendingPaths.Clear();
            _pendingReconcile = false;
        }
        finally { Rw.ExitWriteLock(); }
    }

    /// <summary>
    /// Run a read-only operation against the ready indexes under a read lock. If the index
    /// isn't ready, returns the human-readable status instead (still indexing / needs CLI build).
    /// </summary>
    public static string Query(Func<SegmentedIndex, SegmentedSymbolIndex, string> op)
    {
        EnsureStartedLocked();
        Rw.EnterReadLock();
        try
        {
            if (_state != IndexState.Ready || _text is null || _symbols is null)
                return StatusMessage();
            return op(_text, _symbols);
        }
        finally { Rw.ExitReadLock(); }
    }

    /// <summary>
    /// Readiness probe (used by tests and status reporting). Returns the current index refs
    /// when ready; otherwise a status string. Do not run a long search on the returned refs
    /// without a read lock - prefer <see cref="Query"/> for that.
    /// </summary>
    public static bool TryGet(out SegmentedIndex text, out SegmentedSymbolIndex symbols, out string status)
    {
        EnsureStartedLocked();
        Rw.EnterReadLock();
        try
        {
            if (_state == IndexState.Ready && _text is not null && _symbols is not null)
            {
                text = _text; symbols = _symbols; status = "";
                return true;
            }
            text = null!; symbols = null!; status = StatusMessage();
            return false;
        }
        finally { Rw.ExitReadLock(); }
    }

    public static string StatusLine()
    {
        EnsureStartedLocked();
        Rw.EnterReadLock();
        try
        {
            return _state switch
            {
                IndexState.Ready => $"ready - {_text!.DocumentCount:N0} files indexed",
                IndexState.Building => BuildingMessage(),
                _ => CliBuildMessage(),
            };
        }
        finally { Rw.ExitReadLock(); }
    }

    private static string StatusMessage() => _state == IndexState.Building ? BuildingMessage() : CliBuildMessage();

    public static RoslynCSharpAnalyzer CSharp
    {
        get { lock (AnalyzerGate) { return _csharp ??= new RoslynCSharpAnalyzer(Root); } }
    }

    public static ClangCppAnalyzer Cpp
    {
        get { lock (AnalyzerGate) { return _cpp ??= new ClangCppAnalyzer(Root); } }
    }

    /// <summary>Force a full rebuild (the reindex tool). Builds off-lock so searches keep
    /// serving the old index, then swaps atomically. On failure the old index is kept.</summary>
    public static IndexStats Rebuild()
    {
        Log.For(Root).Info("manual reindex requested");
        var built = RepositoryIndexer.Build(Root); // off-lock; old index still serves reads
        Swap(built.Text, built.Symbols);
        Log.For(Root).Info($"manual reindex complete: {built.Stats.Files:N0} files in {built.Stats.Seconds:F1}s");
        return built.Stats;
    }

    // Dispose the previous indexes and install new ones under the write lock (waits for any
    // in-flight search to finish, so a search never touches a disposed mmap).
    private static void Swap(SegmentedIndex text, SegmentedSymbolIndex symbols)
    {
        Rw.EnterWriteLock();
        try
        {
            _text?.Dispose();
            _symbols?.Dispose();
            _snapshot?.Dispose();
            _text = text;
            _symbols = symbols;
            _snapshot = null;
            _csharp = null;
            _cpp = null;
            _state = IndexState.Ready;
        }
        finally { Rw.ExitWriteLock(); }
    }

    public static void EnableLiveIndex(int debounceMs = 1000)
    {
        Rw.EnterWriteLock();
        try
        {
            if (_watcher is not null) return;
            _watcher = new RepositoryWatcher(Root, OnChanges, debounceMs);
            _watcher.Start();
        }
        finally { Rw.ExitWriteLock(); }
    }

    // Bring the index up without blocking a tool call. Runs its own locking; safe to call
    // before taking a read lock (it never holds the read lock across a build).
    private static void EnsureStartedLocked()
    {
        Rw.EnterUpgradeableReadLock();
        try
        {
            if (_state is IndexState.Ready or IndexState.Building) return;

            // Pick up an index built out-of-band (e.g. the user just ran the CLI).
            if (RepositoryIndexer.TryLoad(Root, out var text, out var symbols))
            {
                Rw.EnterWriteLock();
                try { _text = text; _symbols = symbols; _state = IndexState.Ready; }
                finally { Rw.ExitWriteLock(); }
                Log.For(Root).Info($"loaded existing index ({text.DocumentCount:N0} files)");
                return;
            }

            // No index yet: large workspaces are left for a CLI build; small ones index in
            // the background.
            if (ExceedsAutoLimit(out var total))
            {
                Rw.EnterWriteLock();
                try { _state = IndexState.NeedsCliBuild; }
                finally { Rw.ExitWriteLock(); }
                Log.For(Root).Info($"workspace over auto-index limit ({total / 1048576.0:F0} MB); deferring to CLI build");
                return;
            }

            Rw.EnterWriteLock();
            try
            {
                _progressTotalBytes = total;
                _progressFiles = 0;
                _progressBytes = 0;
                _state = IndexState.Building;
            }
            finally { Rw.ExitWriteLock(); }
            Log.For(Root).Info($"background build started ({total / 1048576.0:F0} MB)");
            Task.Run(BackgroundBuild);
        }
        finally { Rw.ExitUpgradeableReadLock(); }
    }

    private static void BackgroundBuild()
    {
        try
        {
            var built = RepositoryIndexer.Build(Root,
                (f, b) => { Volatile.Write(ref _progressFiles, f); Interlocked.Exchange(ref _progressBytes, b); });
            Swap(built.Text, built.Symbols);
            Log.For(Root).Info($"background build complete: {built.Stats.Files:N0} files in {built.Stats.Seconds:F1}s");
            DrainPending();
        }
        catch (Exception ex)
        {
            Rw.EnterWriteLock();
            try { _state = IndexState.NeedsCliBuild; }
            finally { Rw.ExitWriteLock(); }
            Log.For(Root).Error("background build failed; falling back to CLI-build state", ex);
        }
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
        // Capture changes that land during a build so they aren't lost (the build's snapshot
        // may pre-date them and no further event would re-fire).
        Rw.EnterUpgradeableReadLock();
        try
        {
            if (_state == IndexState.Building)
            {
                Rw.EnterWriteLock();
                try
                {
                    if (batch.FullReconcile) _pendingReconcile = true;
                    else _pendingPaths.AddRange(batch.ChangedFullPaths);
                }
                finally { Rw.ExitWriteLock(); }
                return;
            }
            if (_state != IndexState.Ready) return;

            if (!batch.FullReconcile)
            {
                // Targeted incremental: fast, done under the write lock.
                Rw.EnterWriteLock();
                try { ApplyIncremental(batch.ChangedFullPaths, batch.ChangedFullPaths.Count); }
                finally { Rw.ExitWriteLock(); }
                return;
            }

            // Full reconcile (events were lost): mark Building and rebuild off-lock below, so
            // tool calls aren't blocked for the whole rebuild.
            Rw.EnterWriteLock();
            try { _state = IndexState.Building; }
            finally { Rw.ExitWriteLock(); }
        }
        finally { Rw.ExitUpgradeableReadLock(); }

        Log.For(Root).Info("watcher requested full reconcile; rebuilding");
        try
        {
            var built = RepositoryIndexer.Build(Root);
            Swap(built.Text, built.Symbols);
            DrainPending();
        }
        catch (Exception ex)
        {
            Rw.EnterWriteLock();
            try { _state = IndexState.NeedsCliBuild; }
            finally { Rw.ExitWriteLock(); }
            Log.For(Root).Error("full reconcile failed", ex);
        }
    }

    // Caller holds the write lock.
    private static void ApplyIncremental(IReadOnlyList<string> changedFullPaths, int changedCount)
    {
        try
        {
            _snapshot ??= RepositoryIndexer.LoadSnapshot(Root);
            var c = RepositoryIndexer.ApplyChanges(_text!, _symbols!, _snapshot, Root, changedFullPaths);
            RepositoryIndexer.Persist(Root, _text!, _symbols!, _snapshot);
            _csharp = null;
            _cpp = null;
            if (c.Added != 0 || c.Modified != 0 || c.Removed != 0)
                Log.For(Root).Info($"incremental reindex: +{c.Added} ~{c.Modified} -{c.Removed} " +
                                   $"({changedCount} path(s) changed)");
        }
        catch (Exception ex) { Log.For(Root).Error("incremental reindex failed", ex); }
    }

    // Apply changes captured during a build. Loops a bounded number of times to absorb edits
    // that land during the drain itself; anything after that the next watcher event will catch.
    private static void DrainPending()
    {
        for (int round = 0; round < 5; round++)
        {
            List<string> paths;
            bool reconcile;
            Rw.EnterWriteLock();
            try
            {
                reconcile = _pendingReconcile;
                _pendingReconcile = false;
                paths = _pendingPaths.ToList();
                _pendingPaths.Clear();
                if (paths.Count == 0 && !reconcile) return;
                if (!reconcile && _state == IndexState.Ready)
                    ApplyIncremental(paths, paths.Count);
            }
            finally { Rw.ExitWriteLock(); }

            if (reconcile)
            {
                Log.For(Root).Info("draining a full-reconcile request captured during build");
                try { var b = RepositoryIndexer.Build(Root); Swap(b.Text, b.Symbols); }
                catch (Exception ex) { Log.For(Root).Error("drained reconcile failed", ex); return; }
            }
        }
    }
}
