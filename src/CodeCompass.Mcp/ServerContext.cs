using CodeCompass.Core.Changes;
using CodeCompass.Core.Config;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
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
    // Serializes full builds/compactions so two off-lock rebuilds (e.g. a manual reindex racing
    // the watcher) can't target the same segment directory and collide on seg-NNNNN filenames.
    private static readonly SemaphoreSlim BuildGate = new(1, 1);

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

    // Changes observed while a build is running (state == Building) or a manual rebuild is in
    // flight (_rebuilding); drained on completion so they aren't lost.
    private static readonly List<string> _pendingPaths = new();
    private static bool _pendingReconcile;

    // A full off-lock rebuild (the reindex tool) is running while the old index keeps serving.
    // The watcher must capture changes instead of writing them, so it doesn't race the rebuild
    // for the on-disk cache.
    private static bool _rebuilding;

    // A startup reconcile (catching changes made outside the session, e.g. a Perforce sync) is
    // running in the background while the loaded index keeps serving. Surfaced in the status line.
    private static volatile bool _reconciling;
    public static bool IsReconciling => _reconciling;

    public static string Root { get; private set; } = "";

    private static long AutoIndexLimitBytes() => CodeCompassConfig.MaxAutoBytes();

    public static void Init(string root)
    {
        RepositoryWatcher? oldWatcher;
        Rw.EnterWriteLock();
        try
        {
            oldWatcher = _watcher; // dispose after releasing the lock (see StopLiveIndex)
            _watcher = null;
            Root = Path.GetFullPath(root);
            CodeCompassConfig.Load(Root); // per-repo .codecompass.json in effect for the size gate + build
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
            _rebuilding = false;
        }
        finally { Rw.ExitWriteLock(); }
        oldWatcher?.Dispose();
    }

    /// <summary>Stop live indexing and release the watcher (clean shutdown / re-point).</summary>
    public static void StopLiveIndex()
    {
        RepositoryWatcher? w;
        Rw.EnterWriteLock();
        try { w = _watcher; _watcher = null; }
        finally { Rw.ExitWriteLock(); }
        // Dispose OUTSIDE the lock: the watcher drains an in-flight OnChanges callback, which
        // itself needs the lock - disposing under the lock would deadlock.
        w?.Dispose();
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

    // Publish the current state to the per-repo status file so `codecompass statusline` can show it in
    // Claude Code's status area. Lock-free (defensive reads) and the write is offloaded to a background
    // task, so it's safe to call from inside a lock and never stalls indexing/search. Best-effort.
    private static void PublishStatus()
    {
        if (!CodeCompassConfig.StatusLinePublish()) return;
        var state = _state;
        bool reconciling = _reconciling;
        var t = _text;
        int files = 0;
        try { files = t?.DocumentCount ?? 0; } catch { /* index may be mid-swap */ }
        string root = Root;

        string token, text;
        if (reconciling) { token = "reconciling"; text = "refreshing (external changes)…"; }
        else
        {
            switch (state)
            {
                case IndexState.Ready: token = "ready"; text = $"{files:N0} files"; break;
                case IndexState.Building:
                    long total = Interlocked.Read(ref _progressTotalBytes), done = Interlocked.Read(ref _progressBytes);
                    text = total > 0 ? $"indexing {100.0 * done / total:F0}%" : "indexing…"; token = "building"; break;
                case IndexState.NeedsCliBuild: token = "needsCliBuild"; text = "not indexed - run: codecompass index"; break;
                default: token = "idle"; text = "idle"; break;
            }
        }
        var status = new IndexStatus(token, text, files);
        Task.Run(() => IndexStatusFile.Write(root, status));
    }

    public static RoslynCSharpAnalyzer CSharp
    {
        get { lock (AnalyzerGate) { return _csharp ??= new RoslynCSharpAnalyzer(Root); } }
    }

    public static ClangCppAnalyzer Cpp
    {
        get { lock (AnalyzerGate) { return _cpp ??= new ClangCppAnalyzer(Root); } }
    }

    /// <summary>Force a full rebuild (the reindex tool). Builds off-lock so searches keep
    /// serving the old index, then swaps atomically. On failure the old index is kept. While it
    /// runs, watcher changes are captured (not written) so they don't race the rebuild for the
    /// on-disk cache, then drained afterward.</summary>
    public static IndexStats Rebuild()
    {
        Log.For(Root).Info("manual reindex requested");
        BuildGate.Wait(); // one builder at a time; waits out any in-flight rebuild/compaction
        Rw.EnterWriteLock();
        try { _rebuilding = true; } // divert the watcher to the pending queue; waits out any in-flight incremental
        finally { Rw.ExitWriteLock(); }
        try
        {
            var built = RepositoryIndexer.Build(Root); // off-lock; old index still serves reads
            Swap(built.Text, built.Symbols);
            Log.For(Root).Info($"manual reindex complete: {built.Stats.Files:N0} files in {built.Stats.Seconds:F1}s");
            return built.Stats;
        }
        finally
        {
            Rw.EnterWriteLock();
            try { _rebuilding = false; }
            finally { Rw.ExitWriteLock(); }
            BuildGate.Release();
            DrainPending(); // apply whatever the watcher captured during the rebuild (onto new or, on failure, old index)
        }
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
        PublishStatus(); // now Ready (or reconciling, if a startup reconcile is still in flight)
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
                PublishStatus(); // Ready
                // Catch changes made while we weren't watching (a source-control sync, branch switch)
                // by reconciling in the background - the gate/decision runs off-lock in MaybeReconcile.
                Task.Run(MaybeReconcile);
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
                PublishStatus(); // NeedsCliBuild
                return;
            }

            Rw.EnterWriteLock();
            try
            {
                Interlocked.Exchange(ref _progressTotalBytes, total); // read via Interlocked in BuildingMessage
                Volatile.Write(ref _progressFiles, 0);
                Interlocked.Exchange(ref _progressBytes, 0);
                _state = IndexState.Building;
            }
            finally { Rw.ExitWriteLock(); }
            Log.For(Root).Info($"background build started ({total / 1048576.0:F0} MB)");
            PublishStatus(); // Building
            Task.Run(BackgroundBuild);
        }
        finally { Rw.ExitUpgradeableReadLock(); }
    }

    private static void BackgroundBuild()
    {
        BuildGate.Wait(); // serialize against a manual reindex / watcher rebuild
        bool ok = false;
        try
        {
            var built = RepositoryIndexer.Build(Root,
                (f, b) => { Volatile.Write(ref _progressFiles, f); Interlocked.Exchange(ref _progressBytes, b); });
            Swap(built.Text, built.Symbols);
            Log.For(Root).Info($"background build complete: {built.Stats.Files:N0} files in {built.Stats.Seconds:F1}s");
            ok = true;
        }
        catch (Exception ex)
        {
            Rw.EnterWriteLock();
            try { _state = IndexState.NeedsCliBuild; _pendingPaths.Clear(); _pendingReconcile = false; }
            finally { Rw.ExitWriteLock(); }
            Log.For(Root).Error("background build failed; falling back to CLI-build state", ex);
            PublishStatus(); // NeedsCliBuild
        }
        finally { BuildGate.Release(); }
        if (ok) DrainPending();
    }

    // Runs on a background thread after an existing index loads. Decides (off-lock, so the size-check
    // walk doesn't block queries) whether to reconcile external changes, then does it.
    private static void MaybeReconcile()
    {
        try { if (ShouldAutoReconcile()) BackgroundReconcile(); }
        catch (Exception ex) { Log.For(Root).Warn($"startup reconcile skipped: {ex.Message}"); }
    }

    // Auto-reconcile on startup? Tri-state config wins; default is "local and within the auto limit"
    // (network shares and huge repos are left to a manual reindex - a full-tree stat-walk is slow over
    // SMB, and the file watcher is unreliable there anyway).
    private static bool ShouldAutoReconcile()
    {
        var cfg = CodeCompassConfig.AutoReconcile();
        if (cfg == false) return false;
        if (cfg == true) return true;
        if (NetworkPath.IsNetwork(Root)) return false;
        return !ExceedsAutoLimit(out _); // local only; the walk here is cheap on local disk
    }

    // Reconcile the loaded index against the current tree (picks up out-of-session changes), in the
    // background, while the loaded index keeps serving reads. Mirrors BackgroundBuild's guard usage:
    // _rebuilding makes the watcher capture (not apply) changes so it doesn't race the on-disk write.
    private static void BackgroundReconcile()
    {
        Rw.EnterWriteLock();
        try { _rebuilding = true; _reconciling = true; }
        finally { Rw.ExitWriteLock(); }
        PublishStatus(); // reconciling

        BuildGate.Wait(); // serialize against a manual reindex / watcher rebuild
        try
        {
            var u = RepositoryIndexer.Update(Root);
            Swap(u.Text, u.Symbols);
            if (u.Stats.Added != 0 || u.Stats.Modified != 0 || u.Stats.Removed != 0 || u.Stats.FullRebuild)
                Log.For(Root).Info($"startup reconcile applied external changes: +{u.Stats.Added} ~{u.Stats.Modified} -{u.Stats.Removed}" +
                                   (u.Stats.FullRebuild ? " (full rebuild)" : ""));
        }
        catch (Exception ex) { Log.For(Root).Error("startup reconcile failed; keeping the loaded index", ex); }
        finally
        {
            BuildGate.Release();
            Rw.EnterWriteLock();
            try { _rebuilding = false; _reconciling = false; }
            finally { Rw.ExitWriteLock(); }
            PublishStatus(); // back to Ready (Swap published "reconciling" while the flag was still set)
        }
        DrainPending();
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
            if (_state == IndexState.Building || _rebuilding)
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
                // Targeted incremental: fast, done under the write lock. If segments have piled
                // up, escalate to a compacting rebuild (below) instead of just returning.
                bool compact;
                Rw.EnterWriteLock();
                try
                {
                    ApplyIncremental(batch.ChangedFullPaths, batch.ChangedFullPaths.Count);
                    compact = RepositoryIndexer.NeedsCompaction(_text!, _symbols!);
                    if (compact) _state = IndexState.Building;
                }
                finally { Rw.ExitWriteLock(); }
                if (!compact) return;
                Log.For(Root).Info("compacting: segment count high after incremental edits; merging segments");
            }
            else
            {
                // Full reconcile (events were lost): mark Building and rebuild off-lock below, so
                // tool calls aren't blocked for the whole rebuild.
                Rw.EnterWriteLock();
                try { _state = IndexState.Building; }
                finally { Rw.ExitWriteLock(); }
                Log.For(Root).Info("watcher requested full reconcile; rebuilding");
            }
        }
        finally { Rw.ExitUpgradeableReadLock(); }

        // Off-lock so tool calls aren't blocked for the whole operation, but under BuildGate so it
        // can't race a manual reindex for the segment directory. A true reconcile (events were
        // lost) must re-read files (Build); a compaction just merges existing segments.
        BuildGate.Wait();
        bool ok = false;
        try
        {
            SegmentedIndex nt;
            SegmentedSymbolIndex ns;
            if (batch.FullReconcile) { var b = RepositoryIndexer.Build(Root); nt = b.Text; ns = b.Symbols; }
            else { var c = RepositoryIndexer.Compact(Root); nt = c.Text; ns = c.Symbols; }
            Swap(nt, ns);
            ok = true;
        }
        catch (Exception ex)
        {
            Rw.EnterWriteLock();
            try { _state = IndexState.NeedsCliBuild; _pendingPaths.Clear(); _pendingReconcile = false; }
            finally { Rw.ExitWriteLock(); }
            Log.For(Root).Error("reconcile/compaction failed", ex);
        }
        finally { BuildGate.Release(); }
        if (ok) DrainPending();
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
                BuildGate.Wait(); // serialize with any other rebuild
                try { var b = RepositoryIndexer.Build(Root); Swap(b.Text, b.Symbols); }
                catch (Exception ex) { Log.For(Root).Error("drained reconcile failed", ex); return; }
                finally { BuildGate.Release(); }
            }
        }
    }
}
