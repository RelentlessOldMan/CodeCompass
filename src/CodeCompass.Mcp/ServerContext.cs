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
    private static RepositoryWatcher? _watcher;

    // Teardown signal. A long rebuild (RepositoryIndexer.Build) and the semantic queries observe this token;
    // a re-point (Init) or shutdown (StopLiveIndex) cancels it so those operations bail PROMPTLY instead of
    // running to completion - which is what otherwise makes the file-watcher's Dispose barrier (it waits out an
    // in-flight OnChanges rebuild) hang for minutes, and what lets a stale-root query keep pegging a core. A
    // fresh source is armed on each cancel so subsequent work on the new/continuing context is not pre-cancelled.
    private static CancellationTokenSource _shutdownCts = new();

    /// <summary>The current teardown token (see <see cref="_shutdownCts"/>). Read at each cancellable call so a
    /// re-point/shutdown that swaps the source cancels in-flight work started under the previous one.</summary>
    internal static CancellationToken ShutdownToken => _shutdownCts.Token;

    // Cancel any in-flight build/semantic query and arm a fresh token. Lock-free (so it can run before taking Rw
    // and before disposing the watcher, whose Dispose would otherwise block on the very rebuild we're cancelling).
    // The stale source is left for GC rather than disposed: an in-flight op may still read its token for a beat,
    // and a timer-less CancellationTokenSource is cheap to drop.
    private static void CancelInFlight() => Interlocked.Exchange(ref _shutdownCts, new CancellationTokenSource()).Cancel();

    // Idle release (see IdleRelease): a background timer hands back what the server holds only to go faster - the
    // semantic analyzers, the sidecar cache, the write-side path maps - after a stretch without use, so a long session
    // (one of many open at once) doesn't pin hundreds of MB / GB it's no longer using.
    private static long _lastSemanticUseMs;
    private static long _lastQueryMs;
    private static Timer? _evictTimer;

    // Linked external roots (see LinkStore): each is an independently-built index opened read-only and
    // queried alongside the primary. Loaded when the primary becomes Ready and refreshed on reindex, so
    // a `link add` run in a terminal is picked up on the next session/reindex. Guarded by Rw.
    internal readonly record struct IndexHandle(string Root, SegmentedIndex Text, SegmentedSymbolIndex Symbols, bool IsPrimary);

    // One attached external root. If this session won write-ownership (WriteOwnership - a crash-proof OS
    // handle), it also runs a watcher so edits to that root are indexed live, exactly like the project
    // root; otherwise another session owns writing and this one serves it read-only. Text/Symbols are the
    // current federated handles, swapped under Rw when the owner updates.
    private sealed class LinkedRoot
    {
        public required string Root;
        public required SegmentedIndex Text;
        public required SegmentedSymbolIndex Symbols;
        public WriteOwnership? Own;       // non-null => this session owns writing this root
        public RepositoryWatcher? Watcher; // non-null => owner + live-watching
        public volatile string? Gen;      // IndexGeneration the installed handles reflect (see _loadedGen)
        public long RetryPromoteAtMs;     // its watcher couldn't start: don't try taking it over again before this tick
        // Edits a failed update couldn't write (its index briefly unreadable, say): merged into the next one, and retried
        // (see ScheduleRetry). Touched only under BuildGate (OnLinkedChanges); Held mirrors "non-empty" for lock-free readers.
        public readonly HashSet<string> HeldBack = new(StringComparer.Ordinal);
        public volatile bool Held;
        // Paths the share didn't answer for (see _unsurePaths): kept as indexed, looked at again when the retry timer fires.
        // Touched only under BuildGate, like HeldBack; HasUnsure mirrors "non-empty".
        public readonly HashSet<string> Unsure = new(StringComparer.Ordinal);
        public volatile bool HasUnsure;
        public volatile bool NewerFormat; // its index is a newer CodeCompass's: given up (see GiveUpLinkedRoot)
        public volatile string? FailedGen; // a generation that wouldn't load: not retried on every query until it moves
        // Its startup reconcile (see StartLinkedWatch), cancelled and waited for when the root is torn down: left running, it
        // held the index's write lock against a purge and could write a purged index back.
        public CancellationTokenSource? LoadCts;
        public Task? LoadReconcile;

        // Stop the startup reconcile and wait it out (it never takes _linkedGate, so a caller holding that can wait).
        public void StopLoadReconcile()
        {
            if (LoadCts is null) return;
            LoadCts.Cancel();
            try { LoadReconcile?.Wait(); } catch (AggregateException) { } // it logs its own failure
            LoadCts.Dispose();
            LoadCts = null;
        }
    }
    private static List<LinkedRoot> _linked = new();

    /// <summary>How long a linked root whose watcher couldn't start (its folder missing or unreachable) waits before a query
    /// tries to take it over again - each try can stall on an unreachable share. Settable for tests.</summary>
    internal static int LinkedPromoteRetryMs = 60_000;

    // Session focus (manage_links action=focus): when non-null, queries are SCOPED to just these roots
    // (normalized absolute paths) instead of federating across all of them - the "I'm working in repo A
    // today, search only A" lever for a wrapper project that links several giant repos. null => no focus =>
    // federate every root (the default). Deliberately transient/session-lived: a focus is a mode, not
    // persisted config, so it resets on restart and on a re-point (Init clears it). A volatile reference
    // swap makes it readable lock-free from inside a query op, which already holds the Rw read lock and
    // must not re-enter it (NoRecursion would throw).
    private static volatile HashSet<string>? _focus;

    // Live-watch ROLE for the primary root (WriteOwnership - a crash-proof OS handle). Only the holder runs the
    // file watcher and the startup reconcile/prune, so two sessions on one repo don't both re-index every edit.
    // Every other session serves read-only and reloads when the holder writes (see _loadedGen), and takes over
    // the role when the holder exits. Writes THEMSELVES are serialized across processes by IndexWriteLock, which
    // every writer (this server, another session, a terminal `codecompass index/update`) holds per operation.
    private static WriteOwnership? _own;
    private static readonly object _ownGate = new();
    private static bool _liveRequested;
    private static int _liveDebounceMs = 1000;

    // The IndexGeneration token the installed primary index reflects (null = unknown -> reload when possible). A
    // mismatch with the cache's current token means another process committed a write: queries reload to stay
    // fresh, and a local write reloads FIRST - flushing a stale in-memory manifest would orphan that writer's segments.
    private static volatile string? _loadedGen;

    // The watcher lost events on a root where an in-session full rebuild is gated off (network share / over the
    // auto-index limit): results may be stale until a terminal `codecompass update`, and every query says so.
    // Cleared when a fresh update/rebuild is installed (ours, or another process's - judged by meta's timestamp).
    private static volatile bool _eventsLost;
    private static DateTime _eventsLostUtc;

    // Edits held back because another process's write couldn't be reloaded yet (see RefreshIfStaleLocked): every query says
    // so until they land. They're retried on a back-off timer (see ScheduleRetry), not only on this session's next query or
    // edit - with an idle owner that could be a long time, while other sessions read an index without them.
    private static volatile bool _heldBack;
    private static Timer? _retryTimer;
    private static int _retryDelayMs;
    private static bool _holdWantsRebuild; // _pendingReconcile was set by a hold (the index missing or corrupt) - see ClearHoldLocked
    // The paths held back so far, so a retry that holds them again doesn't count them as new edits (see HoldBackLocked).
    // Guarded by the Rw write lock, like _pendingPaths.
    private static readonly HashSet<string> _heldPaths = new(StringComparer.Ordinal);

    // Paths on a network root the share didn't answer for (see RepositoryIndexer.Probe): kept as indexed and looked at again
    // on their own back-off when the retry timer fires (_unsureDue). Kept apart from _pendingPaths, which every drain round
    // re-applies at once, and from _retryDelayMs, which any hold resets. A retry is not a new edit: the model already went
    // when the edit arrived. Guarded by the Rw write lock.
    private static readonly HashSet<string> _unsurePaths = new(StringComparer.Ordinal);
    private static bool _unsureDue;
    private static int _unsureDelayMs;

    // The index on disk was written by a NEWER CodeCompass: this one can't read or write it. Nothing is served (queries say
    // why), Grep runs, and the live-watch role is handed to a session that can write it (see EnterNewerFormat).
    private static volatile bool _newerFormat;

    private static string CacheDir => IndexStore.CacheDirPath(Root);
    private static bool _warnedNetworkCache;

    internal static bool IsLiveWatchOwnerForTest => _own is not null;
    internal static bool HasWatcherForTest { get { Rw.EnterReadLock(); try { return _watcher is not null; } finally { Rw.ExitReadLock(); } } }
    internal static void OnChangesForTest(ChangeBatch batch) => OnChanges(batch);
    internal static bool IsLinkedOwnerForTest(string root) => FindLinked(root)?.Own is not null;
    internal static bool LinkedHasWatcherForTest(string root) => FindLinked(root)?.Watcher is not null;
    internal static bool? WatcherIgnoresDirForTest(string name) { Rw.EnterReadLock(); try { return _watcher?.Ignore.IsIgnoredDirectory(name); } finally { Rw.ExitReadLock(); } }
    internal static bool? LinkedWatcherIgnoresDirForTest(string root, string name) => FindLinked(root)?.Watcher?.Ignore.IsIgnoredDirectory(name);
    internal static void OnLinkedChangesForTest(string root, ChangeBatch batch) => OnLinkedChanges(PathSafety.NormalizeDir(root), batch);
    private static LinkedRoot? FindLinked(string root) => _linked.FirstOrDefault(l => PathEq(l.Root, root));

    private static IndexState _state = IndexState.NotStarted;
    private static int _progressFiles;
    private static long _progressBytes;
    private static long _progressTotalBytes;

    // Changes observed while a build is running (state == Building) or a manual rebuild is in
    // flight (_rebuilding); drained on completion so they aren't lost. Also edits held back because another process's
    // write couldn't be reloaded yet (see RefreshIfStaleLocked) - they ride along with the next batch. A set: a held-back
    // edit is re-added by every batch until it lands. Ordinal, like the watcher: a case-only rename delivers both spellings.
    private static readonly HashSet<string> _pendingPaths = new(StringComparer.Ordinal);
    private static bool _pendingReconcile;

    // A full off-lock rebuild (the reindex tool) is running while the old index keeps serving.
    // The watcher must capture changes instead of writing them, so it doesn't race the rebuild
    // for the on-disk cache.
    // A count, not a flag: a compaction finishing must not clear it for a reconcile that set it and is still waiting on
    // BuildGate (see BackgroundReconcile). Changed only under the Rw write lock (EndRebuildingLocked).
    private static int _rebuilding;
    private static void EndRebuildingLocked() { if (_rebuilding > 0) _rebuilding--; } // > 0: a re-point reset it meanwhile

    // A startup reconcile (catching changes made outside the session, e.g. a Perforce sync) is
    // running in the background while the loaded index keeps serving. Surfaced in the status line.
    private static volatile bool _reconciling;
    public static bool IsReconciling => _reconciling;

    // Re-point generation. Bumped by Init() whenever the served workspace changes. A background build/reconcile
    // captures the epoch when it STARTS and Swap installs its result only if the epoch still matches - otherwise
    // the workspace was re-pointed mid-build and the (now stale-root) result is discarded rather than installed
    // into the new context, which would silently serve the wrong repository. Written under the Rw write lock.
    private static int _epoch;

    public static string Root { get; private set; } = "";

    // Test seams (HardeningReviewTests): drive the re-point epoch guard deterministically without racing threads.
    internal static int EpochForTest => Volatile.Read(ref _epoch);
    internal static bool IsServingForTest
    {
        get { Rw.EnterReadLock(); try { return _text is not null && _state == IndexState.Ready; } finally { Rw.ExitReadLock(); } }
    }
    internal static bool SwapForTest(SegmentedIndex text, SegmentedSymbolIndex symbols, int epoch) =>
        Swap(text, symbols, epoch, IndexGeneration.Read(CacheDir));

    public static void Init(string root)
    {
        // Re-point: cancel any in-flight build/query for the PREVIOUS root FIRST (lock-free), so the oldWatcher
        // disposal below doesn't block for a multi-minute rebuild and a stale-root semantic query stops pegging
        // a core. A fresh token is armed for the new root. Must precede the write lock and the watcher dispose.
        CancelInFlight();
        RepositoryWatcher? oldWatcher;
        RoslynCSharpAnalyzer? oldCs;
        Rw.EnterWriteLock();
        try
        {
            oldWatcher = _watcher; // dispose after releasing the lock (see StopLiveIndex)
            _watcher = null;
            oldCs = _csharp; // ditto - dispose off-lock
            Root = Path.GetFullPath(root);
            CodeCompassConfig.LoadDefault(Root); // the project's .codecompass.json, for every thread of this server
            _text?.Dispose();
            _symbols?.Dispose();
            _snapshot?.Dispose();
            _text = null;
            _symbols = null;
            _snapshot = null;
            _csharp = null;
            _state = IndexState.NotStarted;
            _pendingPaths.Clear();
            _pendingReconcile = false;
            _rebuilding = 0;
            _focus = null; // a focus names the PREVIOUS project's roots - stale after a re-point; start unscoped
            _loadedGen = null;
            _eventsLost = false;
            _heldBack = false;
            _heldPaths.Clear();
            _retryDelayMs = 0;
            _unsurePaths.Clear();
            _unsureDue = false;
            _unsureDelayMs = 0;
            _holdWantsRebuild = false;
            _newerFormat = false;
            _statusOverride = null;
            _epoch++; // re-point: any in-flight build for the previous root is now stale (discarded at Swap)
        }
        finally { Rw.ExitWriteLock(); }
        oldWatcher?.Dispose();
        oldCs?.Dispose();
        Interlocked.Exchange(ref _retryTimer, null)?.Dispose(); // held-back edits belonged to the previous context
        // Off-lock: tearing down linked roots disposes their watchers, which must never happen under Rw
        // (a watcher.Dispose drains an in-flight OnLinkedChanges -> SwapLinked that itself takes Rw).
        DisposeLinkedRoots();

        // Hand back the previous root's live-watch role, then try to take the new root's. Losing it is normal (another
        // session already watches this repo): this one serves read-only and reloads whenever that session writes.
        WriteOwnership? oldOwn;
        lock (_ownGate) { oldOwn = _own; _own = null; }
        oldOwn?.Dispose();
        var own = WriteOwnership.TryAcquire(CacheDir);
        lock (_ownGate) _own = own;

        // Tell the Grep hook a server is serving this root (it redirects only while one is), and stop saying so for
        // the previous root.
        // Release the old marker first: on a re-point to the same root the new one is the same file.
        Interlocked.Exchange(ref _liveness, null)?.Dispose();
        _liveness = ServerLiveness.Mark(Root);
        _liveRequested = false; // a re-point starts unwatched until EnableLiveIndex (as before the role existed)
        if (own is null)
            Log.For(Root).Info("another CodeCompass session is live-indexing this repo; serving it read-only " +
                               "(reloads when that session writes; takes over when it exits)");
        var cacheDir = CacheDir;
        Task.Run(() => AtomicFile.CleanupStaleTemps(cacheDir)); // temps a crashed writer left behind (off the startup path)
        if (!_warnedNetworkCache && NetworkPath.IsNetwork(IndexStore.BaseDir()))
        {
            // The write lock and live-watch role are exclusive file handles; on a share an SMB reconnect can drop a handle
            // server-side while this process still believes it holds it - letting a second machine write the same cache.
            _warnedNetworkCache = true;
            const string warn = "CODECOMPASS_CACHE_DIR points at a network share. Index caches should be on a LOCAL disk: " +
                                "the cross-process write lock relies on local file-handle semantics.";
            Log.Global.Warn(warn);
            Console.Error.WriteLine("[codecompass] " + warn);
        }
    }

    /// <summary>Release everything this session holds: cancel in-flight work, stop watchers, drop linked roots and
    /// the live-watch role. Called when the MCP host stops, so in-flight work is cancelled with the session
    /// and another session can take over live indexing immediately.</summary>
    public static void Shutdown()
    {
        StopLiveIndex();
        Interlocked.Exchange(ref _retryTimer, null)?.Dispose();
        DisposeLinkedRoots();
        WriteOwnership? own;
        lock (_ownGate) { own = _own; _own = null; }
        own?.Dispose();
        Interlocked.Exchange(ref _liveness, null)?.Dispose();
    }

    // This process's "serving Root" marker for the Grep hook (see ServerLiveness).
    private static ServerLiveness? _liveness;

    // Tell the Grep hook whether this server can answer search_code for Root. Marked from Init (the index loads lazily on the
    // first query, so a cold start must already count), withdrawn while the index can't be served here - written by a
    // NEWER CodeCompass, or left for a terminal build - since then Grep is the only search the agent has.
    private static void SetServing(bool serving)
    {
        if (!serving) { Interlocked.Exchange(ref _liveness, null)?.Dispose(); return; }
        if (Volatile.Read(ref _liveness) is not null || string.IsNullOrEmpty(Root)) return;
        var mark = ServerLiveness.Mark(Root);
        if (Interlocked.CompareExchange(ref _liveness, mark, null) is not null) mark?.Dispose(); // another thread marked first
    }

    // A read-only session takes over the live-watch role once its holder exits (the cheap probe is a failed exclusive
    // open of a local file). It then starts the watcher it was asked for, and reconciles once to catch the edits made
    // while nobody was watching.
    private static void MaybePromoteToOwner()
    {
        // Not while a newer CodeCompass's index is on disk: this session couldn't write it, and holding the role would keep
        // a session that can from taking it.
        if (_own is not null || _newerFormat || string.IsNullOrEmpty(Root)) return;
        lock (_ownGate)
        {
            if (_own is not null) return;
            var won = WriteOwnership.TryAcquire(CacheDir);
            if (won is null) return;
            _own = won;
        }
        Log.For(Root).Info("took over live indexing for this repo (the session that held it ended)");
        if (_liveRequested) StartWatcher();
        if (_state == IndexState.Ready) Task.Run(MaybeReconcile);
    }

    // Another process committed a write to the primary cache (its generation token moved): reload so this session
    // serves what's on disk now instead of a silently stale snapshot. Never blocks a query - if this session's own
    // write is in flight (BuildGate busy), that write records the generation it commits and we skip.
    private static void MaybeReloadExternal()
    {
        if (_state != IndexState.Ready || string.IsNullOrEmpty(Root)) return;
        var gen = IndexGeneration.Read(CacheDir);
        if (gen is null || gen == _loadedGen) return;
        if (!BuildGate.Wait(0)) return;
        try
        {
            if (_state != IndexState.Ready || _rebuilding > 0) return;
            gen = IndexGeneration.Read(CacheDir); // read BEFORE loading: a write that lands mid-load just reloads again
            if (gen is null || gen == _loadedGen) return;
            int epoch = Volatile.Read(ref _epoch);
            if (!RepositoryIndexer.TryLoad(Root, out var t, out var s, out var why))
            {
                // A newer CodeCompass rewrote it: stop serving this session's old copy as if it were current. Anything else
                // (a transient lock, mid-replace) is retried on the next query.
                if (why == IndexLoadFailure.NewerFormat) EnterNewerFormat();
                return;
            }
            // Swap checks the sources token itself, under its lock - judged here, it could drop an analyzer a concurrent
            // query just built against the new token.
            if (Swap(t, s, epoch, gen, sourcesChanged: false))
            {
                Log.For(Root).Info("reloaded the index: another CodeCompass process updated it");
                ClearEventsLostIfRefreshed();
                if (HasPendingEdits()) Task.Run(DrainPending); // edits held back until this reload could happen
            }
        }
        finally { BuildGate.Release(); }
    }

    private static bool HasPendingEdits()
    {
        Rw.EnterReadLock();
        try { return _pendingPaths.Count > 0; }
        finally { Rw.ExitReadLock(); }
    }

    private static string NewerFormatMessage() =>
        "This workspace's index was written by a NEWER CodeCompass than this one, so it can't be read here (and won't be " +
        "rebuilt over). Upgrade this CodeCompass install - or, to rebuild it for this version deliberately: " +
        $"codecompass index \"{Root}\"";

    // The index on disk was written by a NEWER CodeCompass (another install updated first): this one can't read or write it.
    // Stop serving the old copy as current (queries say why), let Grep run, and hand the live-watch role to a session that can
    // write it. Not under Rw (it takes it); the watcher is stopped on another thread, since the caller may be its batch.
    private static void EnterNewerFormat()
    {
        RoslynCSharpAnalyzer? oldCs;
        Rw.EnterWriteLock();
        try
        {
            _text?.Dispose(); _symbols?.Dispose(); _snapshot?.Dispose();
            _text = null; _symbols = null; _snapshot = null;
            oldCs = _csharp; _csharp = null;
            _state = IndexState.NotStarted; // the next query re-checks the index (EnsureStartedLocked) and says why
            _loadedGen = null;
            _pendingPaths.Clear(); _pendingReconcile = false; _holdWantsRebuild = false; // nothing here can write them
            _heldBack = false; _heldPaths.Clear();
            _statusOverride = NewerFormatMessage();
        }
        finally { Rw.ExitWriteLock(); }
        oldCs?.Dispose();
        GiveUpLiveIndexing();
    }

    // See EnterNewerFormat. Idempotent: a session already in this state does nothing. The watcher and role released are the
    // ones current NOW - the deferred task leaves alone whatever a re-point (or a rebuild for this version, which clears
    // _newerFormat) put in their place meanwhile.
    private static void GiveUpLiveIndexing()
    {
        if (_newerFormat) return;
        _newerFormat = true;
        SetServing(false);
        Log.For(Root).Warn("this repo's index was written by a newer CodeCompass; stopped serving and live-indexing it here");
        var watcher = _watcher;
        var own = _own;
        int epoch = Volatile.Read(ref _epoch);
        Task.Run(() =>
        {
            bool still;
            Rw.EnterWriteLock();
            try
            {
                still = _newerFormat && epoch == _epoch;
                if (still && ReferenceEquals(_watcher, watcher)) _watcher = null; else watcher = null;
            }
            finally { Rw.ExitWriteLock(); }
            watcher?.Dispose();
            lock (_ownGate)
            {
                if (still && ReferenceEquals(_own, own)) _own = null; else own = null;
            }
            own?.Dispose();
        });
    }

    // Retry held-back edits (see _heldBack) after a back-off: 1 s, doubling to 30 s while the write keeps failing. A retry
    // already due is left alone: re-arming it on every failing edit pushed it back for good while edits kept coming faster
    // than the back-off, and nothing else rebuilds a missing or corrupt index (see DrainPending).
    // unsure: for paths the share didn't answer for, on their own back-off (see _unsurePaths).
    private static void ScheduleRetry(bool unsure = false)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            if (Interlocked.CompareExchange(ref _retryTimer, null, timer) != timer) return; // replaced by a re-point/shutdown
            timer!.Dispose();
            RetryHeldBack();
        }, null, Timeout.Infinite, Timeout.Infinite);
        if (Interlocked.CompareExchange(ref _retryTimer, timer, null) is not null) { timer.Dispose(); return; }
        int delay = unsure
            ? _unsureDelayMs = Math.Clamp(_unsureDelayMs * 2, 1000, 30_000)
            : _retryDelayMs = Math.Clamp(_retryDelayMs * 2, 1000, 30_000);
        timer.Change(delay, Timeout.Infinite); // disposed meanwhile by a re-point/shutdown: returns false, doesn't throw
    }

    private static void RetryHeldBack()
    {
        try
        {
            Rw.EnterWriteLock();
            try { _unsureDue = _unsurePaths.Count > 0; } finally { Rw.ExitWriteLock(); }
            DrainPending();
            foreach (var lr in _linked)
                if (lr.Held || lr.HasUnsure) OnLinkedChanges(lr.Root, new ChangeBatch(Array.Empty<string>(), FullReconcile: false));
        }
        catch (Exception ex) { Log.For(Root).Warn($"retrying held-back edits failed: {ex.Message}"); }
    }

    // Appended to every query result while edits are held back (see _heldBack, LinkedRoot.Held), or a linked root's index
    // turned out to be a newer CodeCompass's. Called under the Rw read lock.
    private static string HeldBackNote()
    {
        var sb = new System.Text.StringBuilder();
        if (_heldBack && (_pendingPaths.Count > 0 || _pendingReconcile))
            sb.Append("\n(Note: recent edits aren't in the index yet - another CodeCompass process updated it and it couldn't " +
                      "be reloaded here; retrying. Results may be STALE until then.)");
        var held = _linked.Where(l => l.Held).Select(l => l.Root).ToList();
        if (held.Count > 0)
            sb.Append($"\n(Note: recent edits in linked root(s) {RootNames(held)} aren't in their index yet - it couldn't be " +
                      "updated just now; retrying. Results from them may be STALE until then.)");
        var newer = _linked.Where(l => l.NewerFormat).Select(l => l.Root).ToList();
        if (newer.Count > 0)
            sb.Append($"\n(Note: linked root(s) {RootNames(newer)} were re-indexed by a NEWER CodeCompass, which this one can't " +
                      "read: their results come from this session's older copy and may be STALE. Upgrade this install.)");
        return sb.ToString();
    }

    // A linked root whose index a NEWER CodeCompass wrote: this session can't read or write it. Stop watching it and hand
    // its live-watch role back, so a session that can takes it over - and never take it again this session; say so on every
    // query (see HeldBackNote). Off-thread, under _linkedGate: the caller may be that root's own watcher batch.
    private static void GiveUpLinkedRoot(string linkedRoot)
    {
        var lr = FindLinked(linkedRoot);
        if (lr is null || lr.NewerFormat) return;
        lr.NewerFormat = true;
        lr.RetryPromoteAtMs = long.MaxValue;
        Log.For(linkedRoot).Warn("this linked root's index was written by a newer CodeCompass; stopped live-indexing it here");
        Task.Run(() =>
        {
            lock (_linkedGate)
            {
                var w = lr.Watcher; lr.Watcher = null;
                w?.Dispose();
                lr.StopLoadReconcile();
                var own = lr.Own; lr.Own = null;
                own?.Dispose();
            }
        });
    }

    // MaybePromoteToOwner for linked roots: one served read-only takes over writing it once its owner has exited, starts
    // the watcher an owner runs, and reconciles once (gated like a fresh link) to catch edits made while unwatched.
    // Without this a linked root was owned only at link time and went silently stale after its owner exited.
    private static void MaybePromoteLinked()
    {
        if (_linked.All(l => l.Own is not null)) return; // the hot path: no lock
        // Never wait for the gate: a link reconcile holding it may be draining a watcher behind a long build, and
        // promotion can just as well happen on the next query.
        if (!Monitor.TryEnter(_linkedGate)) return;
        try
        {
            long now = Environment.TickCount64;
            foreach (var lr in _linked)
            {
                if (lr.Own is not null || now < lr.RetryPromoteAtMs) continue;
                var won = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(lr.Root));
                if (won is null) continue; // still owned elsewhere
                lr.Own = won;
                if (StartLinkedWatch(lr))
                    Log.For(lr.Root).Info("took over live indexing for this linked root (the session that held it ended)");
            }
        }
        finally { Monitor.Exit(_linkedGate); }
    }

    // A linked root this session just took ownership of: live-watch it (off Rw - see _linkedGate) with ITS OWN ignore rules,
    // and reconcile it once (gated like a fresh link) for the edits made while nobody watched. If the watcher can't start -
    // the folder is gone, a drive unplugged, a share unreachable - hand the ownership back and serve the root read-only:
    // owned but unwatched, nothing would ever update it, and no other session could take it over. A later query retries
    // after a back-off (each try can stall on an unreachable share). Caller holds _linkedGate.
    private static bool StartLinkedWatch(LinkedRoot lr)
    {
        var linkedRoot = lr.Root;
        RepositoryWatcher? watcher = null;
        try
        {
            watcher = new RepositoryWatcher(linkedRoot, b => OnLinkedChanges(linkedRoot, b), 1000, IgnoreRules.ForRoot(linkedRoot));
            watcher.Start();
        }
        catch (Exception ex)
        {
            watcher?.Dispose();
            lr.Own?.Dispose();
            lr.Own = null;
            lr.RetryPromoteAtMs = Environment.TickCount64 + LinkedPromoteRetryMs;
            Log.For(linkedRoot).Warn($"can't watch this linked root ({ex.GetType().Name}: {ex.Message}); serving it read-only, retrying later");
            return false;
        }
        lr.Watcher = watcher;
        // ShutdownToken now: a re-point or shutdown before the reconcile task starts must still cancel it. Unlinking the root
        // cancels it too, and waits for it (StopLoadReconcile).
        lr.LoadCts = CancellationTokenSource.CreateLinkedTokenSource(ShutdownToken);
        var ct = lr.LoadCts.Token;
        lr.LoadReconcile = Task.Run(() => ReconcileLinkedOnLoad(linkedRoot, ct));
        return true;
    }

    // Same freshness rule for linked roots: whoever writes a linked root (its owner here, another session, or the
    // CLI), every session federating it reloads on the next query.
    private static void MaybeReloadLinked()
    {
        var linked = _linked;
        foreach (var lr in linked)
        {
            var gen = IndexGeneration.Read(IndexStore.CacheDirPath(lr.Root));
            if (gen is null || gen == lr.Gen || gen == lr.FailedGen) continue;
            if (!BuildGate.Wait(0)) return;
            try
            {
                gen = IndexGeneration.Read(IndexStore.CacheDirPath(lr.Root));
                if (gen is null || gen == lr.Gen || gen == lr.FailedGen) continue;
                if (RepositoryIndexer.TryLoad(lr.Root, out var t, out var s, out var why))
                {
                    SwapLinked(lr.Root, t, s, gen, sourcesChanged: false); // SwapLinked checks the sources token itself
                    if (lr.NewerFormat)
                    {
                        // Readable again (re-indexed by this version): no longer given up - a query may take it over again.
                        lr.NewerFormat = false;
                        lr.RetryPromoteAtMs = 0;
                    }
                    lr.FailedGen = null;
                }
                else if (why == IndexLoadFailure.NewerFormat)
                {
                    lr.FailedGen = gen; // tried again once it moves, not on every query
                    GiveUpLinkedRoot(lr.Root); // its old copy keeps serving, but every answer says so
                }
            }
            catch (Exception ex) { Log.For(lr.Root).Warn($"linked root reload skipped: {ex.Message}"); }
            finally { BuildGate.Release(); }
        }
    }

    private static void ClearEventsLostIfRefreshed()
    {
        if (!_eventsLost) return;
        var m = IndexMetaFile.Read(Root);
        if (m is not null && DateTime.TryParse(m.BuiltUtc, System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.RoundtripKind, out var built)
            && built.ToUniversalTime() > _eventsLostUtc)
            _eventsLost = false;
    }

    // Actionable index staleness on EVERY result (not only on a zero - an older indexer under-reports non-empty answers
    // too): a searched root whose index was built by an indexer whose OUTPUT logic is behind this binary. Keyed on the
    // content version, so it stays silent across ordinary product-version upgrades (no cry-wolf). Mirrors the CLI.
    private static string StalenessNote(IReadOnlyList<IndexHandle> handles)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var h in handles)
        {
            try
            {
                var behind = IndexMetaFile.BehindNote(IndexMetaFile.Read(h.Root), h.Root);
                if (behind.Length > 0) sb.Append($"\n(Note: {behind}.)");
            }
            catch { /* best-effort */ }
        }
        return sb.ToString();
    }

    // Appended to every query result while _eventsLost is set (see there), saying why (_staleReason).
    private static string EventsLostNote() => _eventsLost
        ? $"\n(Note: {_staleReason}, so results may be STALE. Refresh from a terminal: codecompass update \"{Root}\")"
        : "";

    private const string LostEventsReason =
        "the file watcher lost change events on this network/large workspace and an automatic full rebuild is disabled here";
    private static volatile string _staleReason = LostEventsReason;

    // Caller holds BuildGate + the cache's IndexWriteLock + the Rw write lock. If another process committed since the
    // installed index was loaded, reload it before mutating - a stale manifest flushed over the newer one would drop
    // that writer's segments from the index. Returns why it changed but can't be reloaded right now (None if fine): then the
    // caller must NOT write - applying the edits to the stale copy and persisting it is exactly that damage, committed as
    // current so it's never retried - and holds the edits back instead (see HoldBackLocked).
    private static IndexLoadFailure RefreshIfStaleLocked()
    {
        var gen = IndexGeneration.Read(CacheDir);
        if (gen is not null && gen == _loadedGen) return IndexLoadFailure.None;
        if (!RepositoryIndexer.TryLoad(Root, out var t, out var s, out var why))
        {
            Log.For(Root).Warn($"the index changed on disk but could not be reloaded ({why}); holding back edits");
            // Its generation still says whether that writer changed sources: if so, the C# model is stale either way.
            if (SourcesMoved(Root, gen)) { _csharp?.Dispose(); _csharp = null; }
            return why;
        }
        _text?.Dispose(); _symbols?.Dispose(); _snapshot?.Dispose();
        _text = t; _symbols = s; _snapshot = null;
        if (SourcesMoved(Root, gen)) { _csharp?.Dispose(); _csharp = null; }
        _loadedGen = gen;
        Log.For(Root).Info("reloaded the index before applying edits (another CodeCompass process had updated it)");
        return IndexLoadFailure.None;
    }

    // Edits that can't be written because the index on disk can't be reloaded (see RefreshIfStaleLocked): keep them for a
    // retry, and say so on every query. Missing or corrupt on disk also needs a full rebuild (gated like any - see
    // DrainPending). The files changed on disk either way, so a C# model that read them goes (it rebuilds from the sources,
    // not the index) - for newly held edits only: a retry of the same ones mustn't throw a current model away each time.
    // Not for NewerFormat - see EnterNewerFormat. Caller holds the Rw write lock.
    private static void HoldBackLocked(IReadOnlyCollection<string> paths, IndexLoadFailure why)
    {
        var fresh = paths.Where(p => !_heldPaths.Contains(p)).ToList();
        _heldPaths.UnionWith(paths);
        _pendingPaths.UnionWith(paths);
        if ((why is IndexLoadFailure.Missing or IndexLoadFailure.Corrupt) && !_pendingReconcile)
        {
            _pendingReconcile = true;
            _holdWantsRebuild = true; // ...unless a reload succeeds first (see ClearHoldLocked)
        }
        _heldBack = true;
        // A long unreadable spell can pile up a whole tree of edits: past the cap, rebuild instead (see CaptureIfBusy). Which
        // ones are held is no longer known, so the model goes too.
        if (_heldPaths.Count > MaxPendingPaths || _pendingPaths.Count > MaxPendingPaths)
        {
            _heldPaths.Clear(); _heldPaths.TrimExcess();
            _pendingPaths.Clear(); _pendingPaths.TrimExcess();
            _pendingReconcile = true;
            _holdWantsRebuild = false; // wanted for its own sake now: a reload mustn't cancel it
            fresh = paths.ToList();
        }
        if (MayAffectUnindexed(fresh)) { _csharp?.Dispose(); _csharp = null; }
    }

    // A rebuild covered the held-back edits, or the lost-events note speaks for them from here: forget them as held, so a
    // later hold counts an edit to the same file as new (see HoldBackLocked). Takes the Rw write lock.
    private static void EndHold()
    {
        Rw.EnterWriteLock();
        try { _heldBack = false; _heldPaths.Clear(); _retryDelayMs = 0; }
        finally { Rw.ExitWriteLock(); }
    }

    // The index on disk loaded and the held-back edits are being applied: the hold is over. A rebuild it alone asked for
    // (the index was missing or corrupt then) isn't needed any more. Caller holds the Rw write lock.
    private static void ClearHoldLocked()
    {
        _heldBack = false;
        _heldPaths.Clear();
        _retryDelayMs = 0;
        if (_holdWantsRebuild) { _pendingReconcile = false; _holdWantsRebuild = false; }
    }

    // All linked-root LIFECYCLE (load/dispose) is serialized here, and NEVER runs while holding Rw - a
    // RepositoryWatcher.Dispose drains an in-flight callback that itself takes Rw, so disposing a watcher
    // under Rw would deadlock. The pattern throughout: watcher create/dispose happen off-lock; the _linked
    // list and old mmap indexes are swapped/disposed under a brief Rw write. Rw is never held while taking
    // this gate, so there is no lock-ordering hazard with QueryAll (which only takes Rw read).
    private static readonly object _linkedGate = new();
    private static bool _linksChecked;   // have we reconciled the link set at least once this session?
    private static long _linksSig;        // last-seen links.json signature (LinkStore.Signature) - cheap change probe

    // Tear down every linked root: swap _linked empty under Rw, then dispose watchers (off-lock) and the
    // now-unreferenced indexes/ownership. Resets the load-once flag so a re-point reloads fresh.
    private static void DisposeLinkedRoots()
    {
        bool had;
        lock (_linkedGate)
        {
            List<LinkedRoot> old;
            Rw.EnterWriteLock();
            try { old = _linked; _linked = new List<LinkedRoot>(); } finally { Rw.ExitWriteLock(); }
            had = old.Count > 0;
            foreach (var lr in old) lr.Watcher?.Dispose();                      // off-lock: safe drain
            foreach (var lr in old) lr.StopLoadReconcile();
            foreach (var lr in old) { lr.Text.Dispose(); lr.Symbols.Dispose(); lr.Own?.Dispose(); } // no reader holds these post-swap
            _linksChecked = false; // re-point: next query reloads the new project's link set from scratch
        }
        if (had) InvalidateSemanticAnalyzers(); // root set shrank - rebuild analyzers over the primary alone
    }

    // Pick up a `link add`/`remove` done in a terminal WITHOUT restarting the session. links.json lives in
    // the project's cache dir, which is always LOCAL, so a stat is sub-ms: on each query we compare its cheap
    // signature (mtime^length) to the last seen one and only do real work when it changed. The reconcile
    // itself preserves roots we already hold (keeping their ownership + watcher) and only adds/removes the
    // delta - so we never re-acquire ownership of a root we already own (which would fail against our own
    // exclusive handle and demote us to reader). A transient unreadable read keeps the current set intact.
    private static void MaybeReconcileLinks()
    {
        // Also runs fire-and-forget (Task.Run) after builds: contain and log a failure HERE, where its cause is known,
        // instead of leaving a faulted task to surface (detached from it) at some later GC.
        try
        {
            long sig = LinkStore.Signature(Root);
            if (_linksChecked && sig == Volatile.Read(ref _linksSig)) return; // unchanged: the hot path, no lock
            lock (_linkedGate)
            {
                sig = LinkStore.Signature(Root);
                if (_linksChecked && sig == _linksSig) return; // another thread just reconciled it
                if (!LinkStore.TryRead(Root, out var desired)) return; // couldn't read (racing the atomic write) - keep set, retry next query
                ReconcileTo(desired);
                Volatile.Write(ref _linksSig, sig);
                _linksChecked = true;
            }
        }
        catch (Exception ex) { Log.For(Root).Warn($"linked-root reconcile skipped: {ex.Message}"); }
    }

    /// <summary>Apply a link change made in THIS session now, not on the next query: an unlinked root's index is closed,
    /// so `manage_links remove purge=true` can delete it (the all-or-nothing clear refuses while anything holds it).</summary>
    public static void ReconcileLinksNow() => MaybeReconcileLinks();

    // Bring the federated set in line with the desired link list (caller holds _linkedGate). Each newly-added
    // root gets the SAME freshness handling as the project root: try to win crash-proof write-ownership, and
    // if we do, live-watch it (local AND network) and reconcile out-of-session changes on load (gated for
    // network/huge); otherwise serve it read-only from whoever owns it. Roots already present are untouched
    // (their index/ownership/watcher persist); removed roots are torn down. Off-lock except the brief Rw swap.
    private static void ReconcileTo(IReadOnlyList<string> desiredRaw)
    {
        var root = Root;
        var desired = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // de-dup a hand-edited links.json
        foreach (var raw in desiredRaw)
        {
            string norm;
            try { norm = PathSafety.NormalizeDir(raw); } catch { continue; } // skip a bad path
            if (seen.Add(norm)) desired.Add(norm); // same path listed twice -> federate it once, acquire ownership once
        }

        var current = _linked; // mutated only under _linkedGate, which we hold
        var keep = current.Where(lr => desired.Any(d => PathEq(d, lr.Root))).ToList();
        var remove = current.Where(lr => !desired.Any(d => PathEq(d, lr.Root))).ToList();
        var addRoots = desired.Where(d => !current.Any(lr => PathEq(lr.Root, d))).ToList();
        if (remove.Count == 0 && addRoots.Count == 0) return; // signature changed but the set didn't (e.g. a re-add of the same path)

        var added = new List<LinkedRoot>();
        foreach (var linkedRoot in addRoots)
        {
            try
            {
                var gen = IndexGeneration.Read(IndexStore.CacheDirPath(linkedRoot)); // before loading (see MaybeReloadExternal)
                if (!RepositoryIndexer.TryLoad(linkedRoot, out var t, out var s)) continue; // not indexed yet
                var own = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(linkedRoot));
                added.Add(new LinkedRoot { Root = linkedRoot, Text = t, Symbols = s, Own = own, Gen = gen });
            }
            catch (Exception ex) { Log.For(root).Warn($"linked root not loaded: {linkedRoot}: {ex.Message}"); }
        }

        var next = new List<LinkedRoot>(keep.Count + added.Count);
        next.AddRange(keep);
        next.AddRange(added);
        Rw.EnterWriteLock();
        try { _linked = next; } finally { Rw.ExitWriteLock(); }

        foreach (var lr in remove) lr.Watcher?.Dispose();                        // off-lock: safe drain (see note above)
        foreach (var lr in remove) lr.StopLoadReconcile();                       // nothing writes it once it's unlinked
        foreach (var lr in remove) { lr.Text.Dispose(); lr.Symbols.Dispose(); lr.Own?.Dispose(); }

        foreach (var lr in added) // owners live-watch (off-lock) + reconcile out-of-session changes (gated)
            if (lr.Own is not null) StartLinkedWatch(lr);

        InvalidateSemanticAnalyzers(); // the root set changed - the next semantic query rebuilds over it
        Log.For(root).Info($"linked roots reconciled: +{added.Count} -{remove.Count}; now federating {next.Count} " +
                           $"({next.Count(l => l.Own is not null)} owned/watched, {next.Count(l => l.Own is null)} read-only)");
    }

    // Case-insensitive absolute-path equality (both sides absolutized + trailing-separator-trimmed).
    private static bool PathEq(string a, string b) => PathSafety.SameDir(a, b);

    // A linked root we own: a debounced batch of edits -> update THAT root's own index on disk (its own
    // cache/snapshot), then swap the federated handle. Mirrors the project's OnChanges but simpler - a
    // linked root has no compaction-escalation state machine here. Serialized against all builds by BuildGate.
    private static void OnLinkedChanges(string linkedRoot, ChangeBatch batch)
    {
        // Capture the teardown token BEFORE waiting: a re-point/shutdown re-arms ShutdownToken, so re-reading it after
        // the wait would hand this (now stale) callback an uncancelled token and run it to completion.
        var ct = ShutdownToken;
        ArmIdleRelease();
        try { BuildGate.Wait(ct); } catch (OperationCanceledException) { return; }
        SegmentedIndex? nt = null;
        SegmentedSymbolIndex? ns = null;
        var lr = FindLinked(linkedRoot); // null: unlinked meanwhile
        var held = lr?.HeldBack;
        IReadOnlyCollection<string> paths = batch.ChangedFullPaths;
        // Too many held to keep listing (a long unreadable spell under churn): lost events, reconciled like any (see
        // CaptureIfBusy). Gated off on a network/huge root, which then keeps only this batch's paths.
        if (held is { Count: > 0 } && held.Count + paths.Count > MaxPendingPaths)
        {
            held.Clear(); held.TrimExcess();
            batch = batch with { FullReconcile = true };
        }
        if (held is { Count: > 0 })
        {
            var all = new HashSet<string>(held, StringComparer.Ordinal);
            all.UnionWith(paths);
            held.Clear();
            paths = all;
        }
        // A retry (an empty batch from the timer) also looks again at the paths the share didn't answer for. Not a new edit:
        // that edit dropped the model when it arrived.
        HashSet<string>? retried = null;
        if (lr is { Unsure.Count: > 0 } && batch.ChangedFullPaths.Count == 0 && !batch.FullReconcile)
        {
            retried = new HashSet<string>(lr.Unsure, StringComparer.Ordinal);
            retried.ExceptWith(paths);
            lr.Unsure.Clear();
            paths = paths.Concat(retried).ToList();
        }
        try
        {
            if (lr?.NewerFormat == true) return; // given up: a newer CodeCompass's index (see GiveUpLinkedRoot)
            if (paths.Count == 0 && !batch.FullReconcile) return; // a retry with nothing left to apply
            using var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(linkedRoot), ct);
            bool affects, untouched = false;
            if (batch.FullReconcile)
            {
                // Lost events on a network/huge linked root: a full in-session rebuild is gated off, same as the
                // project root (see OnChanges). A terminal `codecompass update` refreshes it; we reload on its commit.
                if (!ShouldAutoReconcile(linkedRoot))
                {
                    held?.UnionWith(paths); // not written: kept for the next update
                    wl.SkipBump();
                    Log.For(linkedRoot).Warn("watcher lost events on this linked root; skipped an in-session full rebuild " +
                                             $"(network/large) - run: codecompass update \"{linkedRoot}\"");
                    return;
                }
                var b = RepositoryIndexer.Build(linkedRoot, ct: ct); nt = b.Text; ns = b.Symbols;
                affects = true;
            }
            else
            {
                var u = RepositoryIndexer.UpdatePaths(linkedRoot, paths, ct); nt = u.Text; ns = u.Symbols;
                untouched = u.Stats.Untouched; // nothing written (a retry the share still didn't answer, say): nothing to publish
                // A path naming a C# input, or one the index found changed under a folder or a vanished path - not an
                // editor's vanished temp file or a folder whose timestamp moved (see ApplyIncremental).
                affects = RoslynCSharpAnalyzer.MayAffect(retried is null ? paths : paths.Where(p => !retried.Contains(p)).ToList())
                          || u.Stats.FullRebuild || u.Stats.CSharpInputsChanged;
                if (lr is not null)
                {
                    bool wasUnsure = lr.Unsure.Count > 0 || retried is not null;
                    lr.Unsure.ExceptWith(paths);
                    lr.Unsure.UnionWith(u.Stats.Unsure);
                    if (lr.Unsure.Count > 0)
                    {
                        if (!wasUnsure)
                            Log.For(linkedRoot).Warn($"the share didn't answer for {u.Stats.Unsure.Count} changed path(s); kept as indexed, retrying");
                        ScheduleRetry(unsure: true);
                    }
                    else if (wasUnsure) Log.For(linkedRoot).Info("the share answered for every path it hadn't; the index caught up");
                }
            }
            if (!affects) wl.SourcesUnchanged();
            string? gen;
            // What was loaded is what's on disk, under its current generation: other sessions have nothing to reload.
            if (untouched) { wl.SkipBump(); gen = IndexGeneration.Read(IndexStore.CacheDirPath(linkedRoot)); }
            else gen = wl.Commit();
            SwapLinked(linkedRoot, nt, ns, gen, sourcesChanged: affects);
            nt = null; ns = null; // ownership transferred to _linked
        }
        catch (OperationCanceledException) { Log.For(linkedRoot).Info("linked root update canceled (re-point/shutdown)"); }
        catch (Exception ex)
        {
            if (RepositoryIndexer.TryLoad(linkedRoot, out var lt, out var ls, out var why)) { lt.Dispose(); ls.Dispose(); }
            if (why == IndexLoadFailure.NewerFormat)
            {
                // A newer CodeCompass's index: nothing this session writes can land - hand the root to one that can.
                held?.Clear();
                GiveUpLinkedRoot(linkedRoot);
            }
            else
            {
                // Couldn't be written now (briefly unreadable, say): keep the edits for the next update or a retry rather
                // than lose them. The files changed on disk either way, so a C# model that read them goes - once, for new
                // edits, not on every retry of the same ones.
                held?.UnionWith(retried is null ? paths : paths.Where(p => !retried.Contains(p)));
                if (retried is not null) lr!.Unsure.UnionWith(retried); // not looked at: still to retry
                if (batch.FullReconcile || MayAffectUnindexed(batch.ChangedFullPaths)) InvalidateSemanticAnalyzers(); // lost events: anything may have changed
                if (held is { Count: > 0 }) ScheduleRetry();
                else if (lr is { Unsure.Count: > 0 }) ScheduleRetry(unsure: true);
            }
            Log.For(linkedRoot).Error("linked root live update failed" + (held is { Count: > 0 } ? "; holding its edits for a retry" : ""), ex);
        }
        finally
        {
            nt?.Dispose(); ns?.Dispose();
            if (lr is not null) { lr.Held = held is { Count: > 0 }; lr.HasUnsure = lr.Unsure.Count > 0; }
            BuildGate.Release();
        }
    }

    // Could these paths have changed what the C# model read, with no word from the index (it couldn't be written)? Judged
    // conservatively: a C# input by name, or a path that isn't an existing file - a folder, or something gone.
    private static bool MayAffectUnindexed(IEnumerable<string> paths) =>
        paths.Any(p => SemanticInputs.IsCSharpInput(p) || !File.Exists(p));

    // Install fresh indexes for a linked root under the write lock (so no in-flight federated read touches
    // a disposed mmap), disposing the old ones. If the root was unlinked meanwhile, the new ones are dropped.
    // sourcesChanged false (a reconcile or edit that can't have changed what the C# analyzer read) keeps it - see Swap.
    private static void SwapLinked(string linkedRoot, SegmentedIndex text, SegmentedSymbolIndex symbols, string? gen,
                                   bool sourcesChanged = true)
    {
        SegmentedIndex? oldT = null; SegmentedSymbolIndex? oldS = null; bool installed = false, incomplete = false;
        Rw.EnterWriteLock();
        try
        {
            var lr = _linked.FirstOrDefault(l => PathEq(l.Root, linkedRoot));
            if (lr is not null) { oldT = lr.Text; oldS = lr.Symbols; lr.Text = text; lr.Symbols = symbols; lr.Gen = gen; installed = true; }
            incomplete = IncompleteModelDueLocked(); // under the write lock: no query is inside the analyzer
        }
        finally { Rw.ExitWriteLock(); }
        oldT?.Dispose(); oldS?.Dispose();
        if (!installed) { text.Dispose(); symbols.Dispose(); } // unlinked while updating
        // The C# analyzer spans this linked root too, so if its sources changed, drop the cached model so the next
        // semantic query rebuilds over the fresh sources (lazy, off the query path). See Swap.
        if (sourcesChanged || incomplete || SourcesMoved(linkedRoot, gen)) InvalidateSemanticAnalyzers();
    }

    // Reconcile a linked root against its current tree on load (out-of-session changes), gated exactly
    // like the project root: local + within the auto limit -> reconcile now; network/huge -> deferred to
    // a manual `codecompass update "<root>"` (a full stat-walk of a huge/network tree is too slow to auto-run).
    // <paramref name="ct"/>: the teardown token from when the root was taken on - read later, after a re-point or shutdown,
    // it would be the next context's uncancelled one, and this reconcile would run on into it.
    private static void ReconcileLinkedOnLoad(string linkedRoot, CancellationToken ct)
    {
        try
        {
            // ShouldAutoReconcile reads the linked root's own config via ReadFrom (not the ambient _current),
            // and Update() reloads config for its root internally under BuildGate - so no Load() here, which
            // would race the project root's ambient config from this background thread.
            if (!ShouldAutoReconcile(linkedRoot)) return;
            ReconcileLinked(linkedRoot, ct);
        }
        catch (OperationCanceledException) { Log.For(linkedRoot).Info("linked root reconcile canceled (re-point/shutdown)"); }
        catch (Exception ex) { Log.For(linkedRoot).Warn($"linked root reconcile skipped: {ex.Message}"); }
    }

    private static void ReconcileLinked(string linkedRoot, CancellationToken ct)
    {
        BuildGate.Wait(ct);
        try
        {
            using var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(linkedRoot), ct);
            LinkedReconcileHoldForTest?.Invoke(linkedRoot, ct);
            var u = RepositoryIndexer.Update(linkedRoot, ct: ct);
            bool changed = u.Stats.Added != 0 || u.Stats.Modified != 0 || u.Stats.Removed != 0 || u.Stats.FullRebuild;
            if (!changed) wl.SourcesUnchanged();
            SwapLinked(linkedRoot, u.Text, u.Symbols, wl.Commit(), sourcesChanged: changed);
        }
        finally { BuildGate.Release(); }
    }

    /// <summary>Test seam: runs inside a linked root's reconcile while it holds the root's write lock, given the reconcile's
    /// cancellation token - a test parks it there to race an unlink against it.</summary>
    internal static Action<string, CancellationToken>? LinkedReconcileHoldForTest;

    /// <summary>Test seam: run a linked root's startup reconcile synchronously, past its auto-reconcile gate.</summary>
    internal static void RunLinkedStartupReconcileNow(string linkedRoot) => ReconcileLinked(PathSafety.NormalizeDir(linkedRoot), ShutdownToken);

    // Normalize a root path for value comparison against the focus set (absolute, no trailing separator) -
    // identical rule to RootScope.Normalize, which produces the stored focus entries.
    private static string NormalizeRoot(string p) => RootScope.Normalize(p);

    /// <summary>Scope subsequent queries to <paramref name="roots"/> (normalized absolute paths). An empty
    /// set clears focus (back to federating all roots). See <see cref="_focus"/>.</summary>
    internal static void SetFocus(IEnumerable<string> roots)
    {
        var set = new HashSet<string>(roots.Select(NormalizeRoot), StringComparer.OrdinalIgnoreCase);
        _focus = set.Count > 0 ? set : null;
    }

    /// <summary>Clear the session focus - queries federate across all roots again.</summary>
    internal static void ClearFocus() => _focus = null;

    /// <summary>The roots currently in focus (empty => no focus is set, all roots are queried).</summary>
    internal static IReadOnlyCollection<string> CurrentFocus =>
        (IReadOnlyCollection<string>?)_focus ?? Array.Empty<string>();

    // Appended to EVERY scoped query result (even a "no match") so a narrowed search is never misread as
    // "doesn't exist" - the same honesty discipline as the coverage caveats. Empty when no focus is active
    // or when the focus happens to select every root (nothing excluded). Called from inside the read lock.
    private static string FocusDisclosure(int totalRoots, IReadOnlyList<IndexHandle> inScope)
    {
        var focus = _focus;
        if (focus is null) return "";
        // A focused root with no loaded index (linked but never built, or deferred as too large) is NOT searched - say
        // so, or its absence reads as a deliberate exclusion and a zero as "not in that repo".
        int notLoaded = Math.Max(0, focus.Count - inScope.Count);
        int excluded = totalRoots - inScope.Count;
        if (excluded <= 0 && notLoaded == 0) return "";
        var sb = new System.Text.StringBuilder($"\n(Scoped to {RootNames(inScope.Select(h => h.Root))}");
        if (excluded > 0) sb.Append($"; {excluded} other root(s) excluded from this search");
        if (notLoaded > 0) sb.Append($"; {notLoaded} focused root(s) have no index yet and were NOT searched - build with `codecompass index \"<root>\"`");
        return sb.Append(". Clear with manage_links action=focus.)").ToString();
    }

    // Folder names, unless two share one (C:\a\src and D:\b\src) - then full paths, so the scope is unambiguous.
    internal static string RootNames(IEnumerable<string> roots)
    {
        var norm = roots.Select(NormalizeRoot).ToList();
        var names = norm.Select(r => Path.GetFileName(r)).ToList();
        bool clash = names.Distinct(StringComparer.OrdinalIgnoreCase).Count() < names.Count;
        return string.Join(", ", clash ? norm : names);
    }

    /// <summary>
    /// Federated read: run <paramref name="op"/> against the primary index plus every loaded linked
    /// index, under the read lock. The op merges results itself (linked hits are shown with absolute
    /// paths - see the tools). Returns the human-readable status if the primary index isn't ready.
    /// When a session focus is set, the handle set is restricted to the focused roots and the result
    /// discloses what was excluded.
    /// </summary>
    /// <para>The op receives ONE cancellation token for its whole run: the teardown token captured once here (a
    /// re-point re-arms <see cref="ShutdownToken"/>, so re-reading it mid-query would hand later stages a fresh,
    /// uncancelled token and let a stale query run to completion while Init waits on the write lock), linked with the
    /// MCP request's own token (the client cancelled or timed out the call - stop burning the Roslyn pass).</para>
    internal static string QueryAll(Func<IReadOnlyList<IndexHandle>, CancellationToken, string> op, CancellationToken requestCt = default)
    {
        var teardown = ShutdownToken;
        using var linkedCts = requestCt.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(teardown, requestCt) : null;
        var ct = linkedCts?.Token ?? teardown;
        TouchQuery();
        EnsureStartedLocked();
        MaybePromoteToOwner();  // take over live indexing if the session that held it has exited
        MaybeReloadExternal();  // another process (session / terminal command) committed a write -> serve it, not a stale copy
        MaybeReconcileLinks(); // cheap stat; picks up a terminal `link add`/`remove` and guarantees the query sees the current set
        MaybePromoteLinked();  // take over a linked root whose owning session has exited
        MaybeReloadLinked();
        Rw.EnterReadLock();
        try
        {
            if (_state != IndexState.Ready || _text is null || _symbols is null) return StatusMessage();
            var all = new List<IndexHandle>(1 + _linked.Count) { new(Root, _text, _symbols, IsPrimary: true) };
            foreach (var lr in _linked) all.Add(new IndexHandle(lr.Root, lr.Text, lr.Symbols, IsPrimary: false));

            // Session focus: restrict the federated set to the roots the user scoped to. null => all roots.
            var focus = _focus;
            var handles = focus is null ? all : all.Where(h => focus.Contains(NormalizeRoot(h.Root))).ToList();
            if (focus is not null && handles.Count == 0)
                // Focus names only root(s) that aren't indexed/loaded yet - say so rather than let the op
                // return a bare "no match" over nothing, which would read as "the symbol doesn't exist."
                return "Focus is set, but none of the focused root(s) are indexed/loaded. Clear it with " +
                       "manage_links action=focus (no path), or build the focused root's index.";
            string focusNote = FocusDisclosure(all.Count, handles) + EventsLostNote() + HeldBackNote() + StalenessNote(handles);

            // Every tool funnels through here. An unexpected throw inside op (an analyzer edge case, a corrupt
            // segment surfacing mid-read, an OOM in a semantic pass) would otherwise escape to the MCP framework
            // and serialize a raw stack trace - leaking internal cache/repo paths and giving the agent an
            // unactionable error. Contain it to a short, path-free message and log the detail for diagnosis.
            try { return op(handles, ct) + focusNote; }
            catch (OperationCanceledException) when (requestCt.IsCancellationRequested && !teardown.IsCancellationRequested)
            {
                return "The request was cancelled.";
            }
            catch (OperationCanceledException)
            {
                // The teardown token tripped mid-query (a re-point or shutdown), so a long semantic pass bailed.
                // This is expected, not a failure - return a neutral status instead of the alarming
                // "rebuild the index" message the generic handler would give.
                return "The workspace is being re-pointed or the server is shutting down; retry the query in a moment.";
            }
            catch (Exception ex)
            {
                try { Log.Global.Warn($"query op failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
                return $"The query failed unexpectedly. It has been logged; try a narrower query, and if it persists rebuild the index with: codecompass index \"{Root}\"";
            }
        }
        finally { Rw.ExitReadLock(); }
    }

    /// <summary>Is an absolute path inside the primary root or any linked root? Defence for reading a
    /// file a federated result points at. Call under the read lock (via a query op).</summary>
    internal static bool IsUnderAnyRoot(string absPath)
    {
        if (PathSafety.IsUnderOrEqual(absPath, Root)) return true;
        foreach (var lr in _linked) if (PathSafety.IsUnderOrEqual(absPath, lr.Root)) return true;
        return false;
    }

    /// <summary>Stop live indexing and release the watcher (clean shutdown / re-point).</summary>
    public static void StopLiveIndex()
    {
        // Cancel an in-flight rebuild FIRST so the watcher's Dispose barrier (which waits out a running
        // OnChanges) returns promptly on shutdown instead of blocking for the whole rebuild.
        CancelInFlight();
        RepositoryWatcher? w;
        Rw.EnterWriteLock();
        try { w = _watcher; _watcher = null; }
        finally { Rw.ExitWriteLock(); }
        // Dispose OUTSIDE the lock: the watcher drains an in-flight OnChanges callback, which
        // itself needs the lock - disposing under the lock would deadlock.
        w?.Dispose();
    }

    /// <summary>
    /// Readiness probe (test seam). Returns the current index refs when ready; otherwise a status string. Do not run
    /// a long search on the returned refs without a read lock - real queries go through <see cref="QueryAll"/>.
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

    // Why the index can't be served right now when it's neither building nor simply absent (see EnsureStartedLocked).
    private static volatile string? _statusOverride;

    private static string StatusMessage() =>
        _state == IndexState.Building ? BuildingMessage() : _statusOverride ?? CliBuildMessage();

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
        // Best-effort, fire-and-forget: a status-line write failing (disk full, path locked) must never become an
        // unobserved task exception or otherwise disturb the server - swallow it here rather than leaking a fault.
        Task.Run(() => { try { IndexStatusFile.Write(root, status); } catch { } });
    }

    // These getters run inside Query's read lock (find_references), so they can't race the write-locked
    // eviction below - a query holds the read lock across its whole semantic call, and eviction waits
    // for it. Each access stamps "last used" and arms the idle-eviction timer.
    // The analyzers span the project root AND every linked root, so find_references / find_callees resolve
    // across the boundary (a call in the project to a type defined in a linked root binds). They're built
    // lazily from the current root set; a change to that set or to any root's content invalidates them
    // (see InvalidateSemanticAnalyzers) so the next semantic query rebuilds over the new set. The getters
    // run inside a Query/QueryAll read lock, so reading _linked here can't race a swap.
    public static RoslynCSharpAnalyzer CSharp
    {
        get
        {
            lock (AnalyzerGate)
            {
                TouchSemantic();
                if (_csharp is not null) return _csharp;
                var roots = AllRootsSnapshot();
                // Each root's sources token as of this build: another process's write that doesn't move it can't have
                // changed what this model read (see SourcesMoved).
                var tokens = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in roots) tokens[r] = IndexGeneration.SourcesOf(IndexGeneration.Read(IndexStore.CacheDirPath(r)));
                _csharpSources = tokens;
                return _csharp = new RoslynCSharpAnalyzer(roots);
            }
        }
    }

    // The sources tokens the resident analyzer was built against, per root (see CSharp).
    private static volatile Dictionary<string, string?> _csharpSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An incomplete model (a file it couldn't read, a folder its walk couldn't list) is rebuilt on a later write -
    /// the gap usually closes by itself: a lock ends, a share wakes - but at most once per this window, not on EVERY write:
    /// while a linked share stays offline each rebuild costs a full build plus the share's timeout, and comes back incomplete
    /// again. Settable for tests.</summary>
    internal static int IncompleteModelRetryMs = 60_000;

    // Is the resident model incomplete, and due for that retry? Caller holds the Rw write lock (no query is inside it).
    private static bool IncompleteModelDueLocked() =>
        _csharp is { IsIncomplete: true } cs && Environment.TickCount64 - cs.BuiltAtMs >= IncompleteModelRetryMs;

    // Did another process's write to <paramref name="root"/>, now at generation <paramref name="gen"/>, possibly change
    // the sources the resident analyzer read? Only a sources token present in both and equal says no.
    private static bool SourcesMoved(string root, string? gen)
    {
        var now = IndexGeneration.SourcesOf(gen);
        return now is null || !_csharpSources.TryGetValue(root, out var built) || built != now;
    }

    // The project root first (primary, for path display) then every linked root. Call under an Rw read/write
    // lock (the getters hold the query read lock) so _linked isn't swapped mid-read.
    private static IReadOnlyList<string> AllRootsSnapshot()
    {
        var list = new List<string>(1 + _linked.Count) { Root };
        foreach (var lr in _linked) list.Add(lr.Root);
        return list;
    }

    // Drop the cached semantic analyzers so the next semantic query rebuilds them over the current root set
    // (or current content). Same write-lock discipline as EvictIdleAnalyzers - a query holds the read lock
    // across its whole semantic call, so disposal can't pull an analyzer out from under it. Dispose off-lock.
    private static void InvalidateSemanticAnalyzers()
    {
        RoslynCSharpAnalyzer? cs;
        Rw.EnterWriteLock();
        try { cs = _csharp; _csharp = null; }
        finally { Rw.ExitWriteLock(); }
        cs?.Dispose();
    }

    // Stamp semantic use / any query, and make sure the idle-release timer is running (armed once, on first use).
    private static void TouchSemantic()
    {
        Volatile.Write(ref _lastSemanticUseMs, Environment.TickCount64);
        ArmIdleRelease();
    }

    private static void TouchQuery()
    {
        Volatile.Write(ref _lastQueryMs, Environment.TickCount64);
        ArmIdleRelease();
    }

    private static void ArmIdleRelease()
    {
        if (Volatile.Read(ref _evictTimer) is not null) return;
        int idleMin = CodeCompassConfig.SemanticIdleMinutes();
        if (idleMin <= 0) return; // idle release disabled -> keep resident
        long periodMs = Math.Clamp(idleMin * 60_000L / 4, 15_000L, 60_000L); // check a few times per window
        var timer = new Timer(_ => { try { IdleRelease(TimeSpan.FromMinutes(idleMin)); } catch { /* next tick */ } });
        if (Interlocked.CompareExchange(ref _evictTimer, timer, null) is null) timer.Change(periodMs, periodMs);
        else timer.Dispose(); // another thread armed it first
    }

    // Hand back what has gone unused for `idle`: the semantic analyzers (no semantic query), the sidecar cache (no query
    // at all) and each index's write-side path map (no write). Then, if anything went, one compacting gen2 collection:
    // an idle process allocates almost nothing, so without it nothing ever collects and the dropped model - GBs on a
    // Roslyn-size repo - stays committed (measured: private bytes flat after an eviction). Runs on the timer thread, so
    // the pause lands on an idle server, not a query. Returns whether anything was released.
    private static bool IdleRelease(TimeSpan idle)
    {
        // A box short of memory wants it back sooner: every session keeps its own model of the same repo, GBs each on a
        // Roslyn-size one. Then a minute unused is idle enough - the next C# query rebuilds, as after any release.
        if (idle > PressureIdle && UnderMemoryPressure()) idle = PressureIdle;
        long idleMs = (long)idle.TotalMilliseconds;
        bool analyzer = EvictIdleAnalyzers(idleMs);
        long sidecarBytes = Environment.TickCount64 - Volatile.Read(ref _lastQueryMs) >= idleMs ? SidecarCache.Release() : 0;
        int pathMaps = ReleaseIdlePathMaps(idle);
        if (!analyzer && sidecarBytes == 0 && pathMaps == 0) return false;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var what = new List<string>();
        if (analyzer) what.Add("the semantic analyzer");
        if (sidecarBytes > 0) what.Add($"{sidecarBytes / (1024 * 1024)} MB of cached sidecars");
        if (pathMaps > 0) what.Add($"{pathMaps} index path map(s)");
        Log.For(Root).Info($"released idle memory ({string.Join(", ", what)}) after ~{idle.TotalMinutes:0} min unused");
        return true;
    }

    // Drop the semantic analyzers if they've gone unused past the idle window, freeing their (large)
    // in-memory models. Takes the write lock so it can't dispose an analyzer mid-query (find_references
    // holds the read lock for its whole duration); the analyzers rebuild lazily on the next access.
    private static bool EvictIdleAnalyzers(long idleMs)
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastSemanticUseMs) < idleMs) return false;
        if (_csharp is null) return false;

        RoslynCSharpAnalyzer? cs;
        Rw.EnterWriteLock();
        try
        {
            if (Environment.TickCount64 - Volatile.Read(ref _lastSemanticUseMs) < idleMs) return false; // used just now
            cs = _csharp;
            if (cs is null) return false;
            _csharp = null;
        }
        finally { Rw.ExitWriteLock(); }
        cs.Dispose(); // outside the lock; it's detached and unreachable now
        return true;
    }

    // The path maps are touched only by writes, and every write holds BuildGate - so take it (without waiting: a write in
    // progress means the map is in use) and release each index's map that no write has needed for `idle`. Only when one
    // looks idle, and never waiting for the read lock while holding the gate: a reload that finds BuildGate busy takes it
    // for this session's own write and skips, so holding it on every tick (or behind a long query) cost a stale answer.
    private static int ReleaseIdlePathMaps(TimeSpan idle)
    {
        if (!TextIndexes(wait: true)!.Any(t => t.PathMapIdle(idle))) return 0;
        if (!BuildGate.Wait(0)) return 0;
        try
        {
            int n = 0;
            foreach (var t in TextIndexes(wait: false) ?? []) if (t.ReleasePathMapIfIdle(idle)) n++;
            return n;
        }
        finally { BuildGate.Release(); }
    }

    // The primary and linked text indexes, read under the read lock; null when !wait and the lock isn't free right now.
    private static List<SegmentedIndex>? TextIndexes(bool wait)
    {
        if (wait) Rw.EnterReadLock();
        else if (!Rw.TryEnterReadLock(0)) return null;
        try
        {
            var texts = new List<SegmentedIndex>();
            if (_text is not null) texts.Add(_text);
            foreach (var lr in _linked) texts.Add(lr.Text);
            return texts;
        }
        finally { Rw.ExitReadLock(); }
    }

    /// <summary>Test seam: run the idle release now with the given idle window.</summary>
    internal static bool RunIdleReleaseNow(TimeSpan idle) => IdleRelease(idle);

    internal static TimeSpan PressureIdle = TimeSpan.FromMinutes(1); // a test shortens it
    internal static Func<bool>? PressureProbeForTest;
    private const int PressureLoadPercent = 85; // a little under the GC's own high-memory-load mark (90%)

    private static bool UnderMemoryPressure()
    {
        if (PressureProbeForTest is { } probe) return probe();
        return SystemMemory.MemoryLoadPercent() >= PressureLoadPercent;
    }

    /// <summary>Test seam: is the primary index's write-side path map built?</summary>
    internal static bool PrimaryPathMapResidentForTest
    {
        get { Rw.EnterReadLock(); try { return _text?.PathMapResident ?? false; } finally { Rw.ExitReadLock(); } }
    }

    /// <summary>Test seam: are the semantic analyzers currently resident?</summary>
    internal static bool HasResidentSemanticAnalyzers()
    {
        lock (AnalyzerGate) return _csharp is not null;
    }

    /// <summary>Test seam: force the idle-eviction path now, regardless of the idle window.</summary>
    internal static void EvictSemanticAnalyzersNow()
    {
        RoslynCSharpAnalyzer? cs;
        Rw.EnterWriteLock();
        try { cs = _csharp; _csharp = null; }
        finally { Rw.ExitWriteLock(); }
        cs?.Dispose();
    }

    /// <summary>Test seam: run the network/huge-repo startup ignore-prune synchronously.</summary>
    internal static void RunPruneIgnoredNow() => BackgroundPruneIgnored();

    /// <summary>Test seam: run the startup reconcile synchronously (it normally runs in the background after a load).</summary>
    internal static void RunStartupReconcileNow() => BackgroundReconcile();

    /// <summary>Force a full rebuild (the reindex tool). Builds off-lock so searches keep
    /// serving the old index, then swaps atomically. On failure the old index is kept. While it
    /// runs, watcher changes are captured (not written) so they don't race the rebuild for the
    /// on-disk cache, then drained afterward.</summary>
    public static IndexStats? Rebuild()
    {
        Log.For(Root).Info("manual reindex requested");
        var ct = ShutdownToken;
        int epoch = Volatile.Read(ref _epoch);
        BuildGate.Wait(); // one builder at a time; waits out any in-flight rebuild/compaction
        // Never park a tool call behind another PROCESS's write (a terminal `codecompass index` can run for an
        // hour): report it instead - its result is picked up automatically when it commits (MaybeReloadExternal).
        var wl = IndexWriteLock.TryAcquire(CacheDir);
        if (wl is null)
        {
            BuildGate.Release();
            Log.For(Root).Info("manual reindex skipped: another CodeCompass process is writing this index");
            return null;
        }
        Rw.EnterWriteLock();
        try { _rebuilding++; } // divert the watcher to the pending queue; waits out any in-flight incremental
        finally { Rw.ExitWriteLock(); }
        try
        {
            var built = RepositoryIndexer.Build(Root, ct: ct); // off-lock; old index still serves reads
            if (Swap(built.Text, built.Symbols, epoch, wl.Commit()))
            {
                _eventsLost = false;
                Log.For(Root).Info($"manual reindex complete: {built.Stats.Files:N0} files in {built.Stats.Seconds:F1}s");
            }
            return built.Stats;
        }
        catch (OperationCanceledException)
        {
            // Re-point/shutdown during a manual reindex: abandon the rebuild and return an empty stat rather
            // than throwing out of the tool call. The old index keeps serving; the new context (if a re-point)
            // builds fresh.
            Log.For(Root).Info("manual reindex canceled (re-point/shutdown)");
            return new IndexStats(0, 0L, 0L, 0, 0d, 0L, Environment.ProcessorCount);
        }
        finally
        {
            wl.Dispose();
            Rw.EnterWriteLock();
            try { EndRebuildingLocked(); }
            finally { Rw.ExitWriteLock(); }
            BuildGate.Release();
            DrainPending(); // apply whatever the watcher captured during the rebuild (onto new or, on failure, old index)
            // reindex is the manual lever to FORCE a linked-root re-probe: a root that was linked-but-unindexed
            // (large, deferred to `codecompass index`) and has since been built isn't picked up by the signature
            // watch (links.json didn't change), so clear the flag to re-attempt loading it. The safe-diff only
            // retries the not-yet-loaded roots; already-federated ones are untouched.
            lock (_linkedGate) { _linksChecked = false; }
            Task.Run(MaybeReconcileLinks);
        }
    }

    // Dispose the previous indexes and install new ones under the write lock (waits for any
    // in-flight search to finish, so a search never touches a disposed mmap). Returns false (and disposes the
    // incoming indexes, leaving current state untouched) if the workspace was re-pointed since the build that
    // produced them started - see _epoch. <paramref name="gen"/> is the IndexGeneration the new handles reflect.
    // <paramref name="sourcesChanged"/> false (a reconcile that found nothing added, modified or removed) keeps the C#
    // analyzer: it reads the sources, not the index, so it's still current - and on a big solution it may have just been
    // built by the session's first find_references. Edits made meanwhile are drained after the swap, which drops it.
    private static bool Swap(SegmentedIndex text, SegmentedSymbolIndex symbols, int epoch, string? gen, bool sourcesChanged = true)
    {
        RoslynCSharpAnalyzer? oldCs = null;
        bool installed = false;
        Rw.EnterWriteLock();
        try
        {
            if (epoch == _epoch)
            {
                _text?.Dispose();
                _symbols?.Dispose();
                _snapshot?.Dispose();
                _text = text;
                _symbols = symbols;
                _snapshot = null;
                // Also when the root's sources token moved past the one the analyzer was built against: a write that
                // changed nothing itself carries the current token, which may be another writer's newer one. And an
                // incomplete model (a file it couldn't read, a folder it couldn't list) is retried on a later write.
                if (sourcesChanged || SourcesMoved(Root, gen) || IncompleteModelDueLocked())
                {
                    oldCs = _csharp; // stale after a rebuild; dispose off-lock to free their model
                    _csharp = null;
                }
                _state = IndexState.Ready;
                _loadedGen = gen;
                _statusOverride = null;
                _newerFormat = false; // an index this version wrote: the next query may take the live-watch role back
                installed = true;
            }
        }
        finally { Rw.ExitWriteLock(); }

        if (!installed)
        {
            // Re-pointed mid-build: this result belongs to a stale root. Drop it; never touch the current context.
            text.Dispose(); symbols.Dispose();
            Log.For(Root).Info("discarded a rebuild whose workspace was re-pointed mid-flight (stale epoch)");
            return false;
        }
        oldCs?.Dispose();
        SetServing(true);
        // meta.json (path/version/coverage) is written by RepositoryIndexer.Build/Update, which have the
        // walker's over-cap count; Swap must not overwrite it here (it has no coverage data).
        PublishStatus(); // now Ready (or reconciling, if a startup reconcile is still in flight)
        Task.Run(MaybeReconcileLinks); // warm the federated set off-lock (cheap no-op if unchanged)
        return true;
    }

    public static void EnableLiveIndex(int debounceMs = 1000)
    {
        _liveRequested = true;
        _liveDebounceMs = debounceMs;
        // Only the live-watch role holder watches; a read-only session starts its watcher if it takes over the role.
        if (_own is null) return;
        StartWatcher();
    }

    private static void StartWatcher()
    {
        // Root's own ignore rules, not the ambient config (a linked-root write may have left its own loaded).
        var ignore = IgnoreRules.ForRoot(Root);
        Rw.EnterWriteLock();
        try
        {
            if (_watcher is not null) return;
            _watcher = new RepositoryWatcher(Root, OnChanges, _liveDebounceMs, ignore);
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

            // Pick up an index built out-of-band (e.g. the user just ran the CLI). Read the generation BEFORE loading,
            // so a write that lands mid-load is seen as a newer generation and reloaded, never mistaken for this one.
            var gen = IndexGeneration.Read(CacheDir);
            if (RepositoryIndexer.TryLoad(Root, out var text, out var symbols, out var why))
            {
                Rw.EnterWriteLock();
                try { _text = text; _symbols = symbols; _state = IndexState.Ready; _loadedGen = gen; _statusOverride = null; }
                finally { Rw.ExitWriteLock(); }
                _newerFormat = false; // readable again (rebuilt for this version): the next query may take the live-watch role back
                SetServing(true);
                Log.For(Root).Info($"loaded existing index ({text.DocumentCount:N0} files)");
                // Once per session: if this index was built by an indexer whose output logic is behind this
                // binary, a rebuild would materially change results (the "quietly degrading old index" trap).
                // Judged on content version, not product version, so this stays silent across normal upgrades.
                if (IndexMetaFile.IndexerBehind(IndexMetaFile.Read(Root), out int builtCv, out int curCv))
                    Log.For(Root).Warn($"index built by an older indexer (content v{builtCv} < v{curCv}); a rebuild " +
                                       $"would change results (recall/symbols may be under-reported) - run: codecompass index \"{Root}\"");
                PublishStatus(); // Ready
                // Catch changes made while we weren't watching (a source-control sync, branch switch)
                // by reconciling in the background - the gate/decision runs off-lock in MaybeReconcile.
                Task.Run(MaybeReconcile);
                Task.Run(MaybeReconcileLinks); // warm the federated set off-lock (own watchers/reconcile)
                return;
            }

            // An index exists but this binary can't use it right now. Neither case may start a background build: a
            // transient read failure retries on the next query, and an index from a NEWER CodeCompass (another install
            // on this machine updated first) must not be rebuilt over - the two would rebuild each other's forever.
            if (why == IndexLoadFailure.Transient)
            {
                _statusOverride = "The index for this workspace couldn't be read just now (a transient file lock or share " +
                                  "hiccup). Retry the query in a moment.";
                return;
            }
            if (why == IndexLoadFailure.NewerFormat)
            {
                _statusOverride = NewerFormatMessage();
                GiveUpLiveIndexing(); // search_code can't answer here (so Grep runs), and a session that can write it takes over
                return;
            }

            // No index yet: large workspaces are left for a CLI build; small ones index in
            // the background.
            if (ExceedsAutoLimit(out var total))
            {
                Rw.EnterWriteLock();
                try { _state = IndexState.NeedsCliBuild; }
                finally { Rw.ExitWriteLock(); }
                SetServing(false);
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
        var ct = ShutdownToken;
        int epoch = Volatile.Read(ref _epoch);
        try { BuildGate.Wait(ct); } // serialize against a manual reindex / watcher rebuild
        catch (OperationCanceledException) { return; }
        bool ok = false;
        try
        {
            using var wl = IndexWriteLock.Acquire(CacheDir, ct);
            // Another session (or a terminal build) may have produced this index while we queued for the write lock -
            // load theirs rather than building the same thing twice.
            if (RepositoryIndexer.TryLoad(Root, out var existing, out var existingSymbols, out var why))
            {
                wl.SkipBump();
                ok = Swap(existing, existingSymbols, epoch, IndexGeneration.Read(CacheDir));
                if (ok) Log.For(Root).Info("loaded the index another CodeCompass process built while this one waited");
            }
            else if (why is IndexLoadFailure.NewerFormat or IndexLoadFailure.Transient)
            {
                // Never built over: a newer install's index (see EnterNewerFormat), or one that's just briefly unreadable.
                wl.SkipBump();
                Rw.EnterWriteLock();
                try { _state = IndexState.NotStarted; } // the next query looks again
                finally { Rw.ExitWriteLock(); }
                if (why == IndexLoadFailure.NewerFormat) EnterNewerFormat();
            }
            else
            {
                var built = RepositoryIndexer.Build(Root,
                    (f, b) => { Volatile.Write(ref _progressFiles, f); Interlocked.Exchange(ref _progressBytes, b); }, ct);
                ok = Swap(built.Text, built.Symbols, epoch, wl.Commit());
                if (ok) Log.For(Root).Info($"background build complete: {built.Stats.Files:N0} files in {built.Stats.Seconds:F1}s");
            }
        }
        catch (OperationCanceledException)
        {
            // Re-point/shutdown cancelled this build; the context is being replaced (Init resets state for the
            // new root) or torn down. Abandon quietly - do NOT fall back to the CLI-build state.
            Log.For(Root).Info("background build canceled (re-point/shutdown)");
        }
        catch (Exception ex)
        {
            Rw.EnterWriteLock();
            try { _state = IndexState.NeedsCliBuild; _pendingPaths.Clear(); _pendingReconcile = false; _holdWantsRebuild = false; _heldPaths.Clear(); _heldBack = false; }
            finally { Rw.ExitWriteLock(); }
            SetServing(false);
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
        try
        {
            if (_own is null) return; // the live-watch role holder reconciles; a read-only session reloads its result
            if (ShouldAutoReconcile(Root)) { BackgroundReconcile(); return; }
            // The full stat-walk reconcile is gated off (network share or huge repo). Unless the user
            // explicitly disabled auto-reconcile, still run the CHEAP ignore-only prune: a stale index (built
            // by an older/looser version) can hold now-ignored pollution (a rival tool's .claude cache dump, a
            // dir since added to CODECOMPASS_IGNORE), and the query-time filter only HIDES it - this physically
            // drops it, with no re-walk, so even network/huge repos self-heal on startup.
            if (CodeCompassConfig.AutoReconcile(RootConfig()) != false) BackgroundPruneIgnored(); // drains captured edits after
            else DrainPending(); // edits the watcher captured before the index loaded (see CaptureIfBusy)
        }
        catch (Exception ex) { Log.For(Root).Warn($"startup reconcile skipped: {ex.Message}"); }
    }

    // Auto-reconcile (on startup, or after the watcher lost events)? Tri-state config wins; default is "local and within the
    // auto limit" (network shares and huge repos are left to a manual reindex - a full-tree stat-walk is slow over SMB, and
    // the file watcher is unreliable there anyway). Reads THAT root's own config, for the project root and a linked root
    // alike - never the ambient one, which is whichever root was written last.
    private static bool ShouldAutoReconcile(string root)
    {
        var cfg = CodeCompassConfig.ReadFrom(root) ?? new RepoConfig();
        var ar = CodeCompassConfig.AutoReconcile(cfg);
        if (ar == false) return false;
        if (ar == true) return true;
        if (NetworkPath.IsNetwork(root)) return false;
        return !RepositoryIndexer.ExceedsAutoLimit(root, cfg, out _);
    }

    // Reconcile the loaded index against the current tree (picks up out-of-session changes), in the
    // background, while the loaded index keeps serving reads. Mirrors BackgroundBuild's guard usage:
    // _rebuilding makes the watcher capture (not apply) changes so it doesn't race the on-disk write.
    private static void BackgroundReconcile()
    {
        var ct = ShutdownToken;
        int epoch = Volatile.Read(ref _epoch);
        Rw.EnterWriteLock();
        try { _rebuilding++; _reconciling = true; }
        finally { Rw.ExitWriteLock(); }
        PublishStatus(); // reconciling

        bool gated = false;
        try
        {
            BuildGate.Wait(ct); // serialize against a manual reindex / watcher rebuild
            gated = true;
            using var wl = IndexWriteLock.Acquire(CacheDir, ct);
            var u = RepositoryIndexer.Update(Root, ct: ct);
            bool changed = u.Stats.Added != 0 || u.Stats.Modified != 0 || u.Stats.Removed != 0 || u.Stats.FullRebuild;
            if (!changed) wl.SourcesUnchanged();
            if (Swap(u.Text, u.Symbols, epoch, wl.Commit(), sourcesChanged: changed))
            {
                _eventsLost = false; // a full walk caught whatever the watcher missed
                if (changed)
                    Log.For(Root).Info($"startup reconcile applied external changes: +{u.Stats.Added} ~{u.Stats.Modified} -{u.Stats.Removed}" +
                                       (u.Stats.FullRebuild ? " (full rebuild)" : ""));
            }
        }
        catch (OperationCanceledException) { Log.For(Root).Info("startup reconcile canceled (re-point/shutdown)"); }
        catch (Exception ex) { Log.For(Root).Error("startup reconcile failed; keeping the loaded index", ex); }
        finally
        {
            if (gated) BuildGate.Release();
            Rw.EnterWriteLock();
            try { EndRebuildingLocked(); _reconciling = false; }
            finally { Rw.ExitWriteLock(); }
            PublishStatus(); // back to Ready (Swap published "reconciling" while the flag was still set)
        }
        DrainPending();
    }

    // Cheap companion to BackgroundReconcile for the network/huge case where the full stat-walk is skipped:
    // physically drop stale now-ignored paths from the on-disk index (no re-walk, no source reads). Same guard
    // pattern (BuildGate + _rebuilding) and Swap as the full reconcile, so it can't race a manual reindex or a
    // live-watch write. No-op (and no swap) when the index is already clean.
    private static void BackgroundPruneIgnored()
    {
        var ct = ShutdownToken;
        int epoch = Volatile.Read(ref _epoch);
        try { BuildGate.Wait(ct); } catch (OperationCanceledException) { return; }
        Rw.EnterWriteLock();
        try { _rebuilding++; }
        finally { Rw.ExitWriteLock(); }
        try
        {
            using var wl = IndexWriteLock.Acquire(CacheDir, ct);
            var u = RepositoryIndexer.PruneIgnored(Root);
            if (u.Pruned > 0)
            {
                // The analyzer walks with the same ignore rules, so it never read the pruned paths: no source it uses changed.
                wl.SourcesUnchanged();
                if (Swap(u.Text, u.Symbols, epoch, wl.Commit(), sourcesChanged: false)) // Swap disposes the handles itself if the workspace was re-pointed
                    Log.For(Root).Info($"pruned {u.Pruned} stale now-ignored path(s) from the index " +
                                       "(an older/looser build had indexed them; query-time filter already hid them)");
            }
            else
            {
                // Already clean, or the load failed (null handles) - drop any extra handles and keep serving. Nothing
                // was rewritten, so don't make every other session reload an unchanged index.
                u.Text?.Dispose(); u.Symbols?.Dispose();
                wl.SkipBump();
            }
        }
        catch (OperationCanceledException) { Log.For(Root).Info("ignore-prune canceled (re-point/shutdown)"); }
        catch (Exception ex) { Log.For(Root).Warn($"ignore-prune skipped: {ex.Message}"); }
        finally
        {
            BuildGate.Release();
            Rw.EnterWriteLock();
            try { EndRebuildingLocked(); }
            finally { Rw.ExitWriteLock(); }
            DrainPending();
        }
    }

    // The auto-index size policy lives in Core (RepositoryIndexer.ExceedsAutoLimit) so the project root
    // (here) and linked roots (`link add`) apply the identical "small -> index, large -> defer" rule.
    private static bool ExceedsAutoLimit(out long totalBytes) =>
        RepositoryIndexer.ExceedsAutoLimit(Root, RootConfig(), out totalBytes);

    // Root's own .codecompass.json - never the ambient config, which a linked-root write switches to that root's.
    private static RepoConfig RootConfig() => CodeCompassConfig.ReadFrom(Root) ?? new RepoConfig();

    /// <summary>Would an in-server rebuild of this workspace exceed the auto-index size limit? The `reindex`
    /// tool uses this to refuse a synchronous multi-minute build that would time out the MCP call (and orphan
    /// the build), pointing the caller at the CLI instead - mirroring the initial-index deferral policy.</summary>
    internal static bool ReindexWouldExceedAutoLimit(out long totalBytes) => ExceedsAutoLimit(out totalBytes);

    /// <summary>Is a ready index installed (lock-free snapshot; for wording a status, not for reading the index)?</summary>
    internal static bool IsServing => _state == IndexState.Ready && _text is not null;

    /// <summary>The CLI-build guidance message (for the reindex tool's over-limit refusal).</summary>
    internal static string CliBuildGuidance() => CliBuildMessage();

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

    // Capture a watcher batch into the pending queue when it can't be applied now, so the edit isn't lost: a build/rebuild owns
    // the cache (state Building, or a rebuild/reconcile in flight) - drained when it finishes - or no index is loaded yet (the
    // watcher starts with the session, the index loads on the first query; or it's left for a terminal build) - drained once
    // one is (see MaybeReconcile). Returns true if captured. A rebuild sets its flag BEFORE queuing on BuildGate, so this is
    // also re-checked after the gates.
    private static bool CaptureIfBusy(ChangeBatch batch)
    {
        Rw.EnterUpgradeableReadLock();
        try
        {
            if (_state == IndexState.Ready && _rebuilding == 0) return false;
            if (_newerFormat) return true; // nothing here can write it; a session that can takes over the edits' files
            Rw.EnterWriteLock();
            try
            {
                if (batch.FullReconcile) _pendingReconcile = true;
                else _pendingPaths.UnionWith(batch.ChangedFullPaths);
                if (_pendingPaths.Count > MaxPendingPaths) { _pendingPaths.Clear(); _pendingReconcile = true; }
                if (_pendingReconcile) _holdWantsRebuild = false; // wanted for its own sake now: a reload mustn't cancel it
            }
            finally { Rw.ExitWriteLock(); }
            return true;
        }
        finally { Rw.ExitUpgradeableReadLock(); }
    }

    // A long wait (for a terminal build) can pile up a whole tree of edits: past this many, rebuild instead. Below it they're
    // applied like any batch - a 2,500-file `git pull` during a build mustn't cost a second full build, or (where in-session
    // rebuilds are gated off) be dropped.
    private static int MaxPendingPaths = 50_000; // not const: a test lowers it
    internal static int MaxPendingPathsForTest { get => MaxPendingPaths; set => MaxPendingPaths = value; }
    internal static int HeldPathCountForTest { get { Rw.EnterReadLock(); try { return _heldPaths.Count; } finally { Rw.ExitReadLock(); } } }
    internal static int LinkedHeldCountForTest(string root) => FindLinked(PathSafety.NormalizeDir(root))?.HeldBack.Count ?? -1;

    private static void OnChanges(ChangeBatch batch)
    {
        // Capture the teardown token and epoch BEFORE any wait: a re-point re-arms ShutdownToken and bumps the epoch,
        // so values read after waiting would belong to the NEW context and let this stale callback run to completion
        // (blocking the watcher's Dispose barrier, and building the new root's tree from an old root's event).
        var ct = ShutdownToken;
        int epoch = Volatile.Read(ref _epoch);
        ArmIdleRelease(); // a write builds the path map: make sure an owner nobody queries still hands it back
        if (CaptureIfBusy(batch)) return;
        if (_state != IndexState.Ready) return;

        // Every write takes the gates in ONE order: BuildGate (this process's builders) -> IndexWriteLock (all
        // processes) -> Rw. Both waits are cancellable, and neither holds Rw, so a long write by another process (a
        // terminal `codecompass index`) only parks this callback - queries keep serving the loaded index meanwhile.
        try { BuildGate.Wait(ct); } catch (OperationCanceledException) { return; }
        bool ok = false, keptServing = false, rebuildingSet = false;
        try
        {
            using var wl = IndexWriteLock.Acquire(CacheDir, ct);
            if (ct.IsCancellationRequested || Volatile.Read(ref _epoch) != epoch) { wl.SkipBump(); return; }
            if (CaptureIfBusy(batch) || _state != IndexState.Ready) { wl.SkipBump(); return; }

            if (!batch.FullReconcile)
            {
                // Targeted incremental: fast, done under the write lock. If segments have piled up, escalate to a
                // compaction instead of just returning.
                bool compact = false, affects = false, untouched = false;
                var failure = IndexLoadFailure.None;
                Rw.EnterWriteLock();
                try
                {
                    // Another process wrote since we loaded: mutate THEIR state, not our stale copy. If it can't be
                    // reloaded yet, write nothing (see HoldBackLocked).
                    failure = RefreshIfStaleLocked();
                    if (failure == IndexLoadFailure.None)
                    {
                        affects = ApplyIncremental(TakePendingLocked(batch.ChangedFullPaths), out untouched);
                        compact = RepositoryIndexer.NeedsCompaction(_text!, _symbols!);
                        // The merge works on its own copy of the index: this one keeps serving, as during any rebuild here
                        // (BackgroundReconcile, Reindex) - not "indexing 0%" - while the watcher's edits wait their turn.
                        if (compact) { _rebuilding++; rebuildingSet = true; }
                    }
                    else if (failure != IndexLoadFailure.NewerFormat) HoldBackLocked(batch.ChangedFullPaths, failure);
                }
                finally { Rw.ExitWriteLock(); }
                if (failure != IndexLoadFailure.None)
                {
                    wl.SkipBump();
                    if (failure == IndexLoadFailure.NewerFormat) EnterNewerFormat();
                    else ScheduleRetry();
                    return;
                }
                if (untouched) wl.SkipBump(); // nothing written: other sessions have nothing to reload
                else
                {
                    if (!affects) wl.SourcesUnchanged(); // other sessions reload the index but keep their C# model
                    _loadedGen = wl.Commit();
                }
                if (!compact) return;
                Log.For(Root).Info("compacting: segment count high after incremental edits; merging segments");
                var c = RepositoryIndexer.Compact(Root, ct);
                // Compact's nested hold reset it. A merge changes no source, so this commit carries the token the one above
                // published - a moved one after a C# edit, which other sessions must see exactly once, not twice.
                wl.SourcesUnchanged();
                ok = Swap(c.Text, c.Symbols, epoch, wl.Commit(), sourcesChanged: false);
            }
            else
            {
                // Events were lost (watcher buffer overflow). Only a full re-walk can be trusted - but on a network share
                // or a repo over the auto-index limit that is an hours-long rebuild, the very work the startup reconcile
                // and the reindex tool refuse to do in-session. Keep serving, and say on every query that results may
                // be stale until a terminal `codecompass update` (whose commit this session then reloads).
                if (!ShouldAutoReconcile(Root))
                {
                    wl.SkipBump();
                    _staleReason = LostEventsReason;
                    _eventsLostUtc = DateTime.UtcNow;
                    _eventsLost = true;
                    Log.For(Root).Warn("watcher lost change events; skipped an in-session full rebuild on this network/large " +
                                       $"workspace - results may be stale until: codecompass update \"{Root}\"");
                    return;
                }
                // As for a compaction, above, the loaded index keeps serving - but it's known to miss the lost edits until the
                // rebuild lands, so every query says so meanwhile (Swap's success clears it; a failure says why, below).
                _staleReason = "the file watcher lost change events and the index is being rebuilt";
                _eventsLostUtc = DateTime.UtcNow;
                _eventsLost = true;
                Rw.EnterWriteLock();
                try { _rebuilding++; rebuildingSet = true; }
                finally { Rw.ExitWriteLock(); }
                Log.For(Root).Info("watcher requested full reconcile; rebuilding");
                var b = RepositoryIndexer.Build(Root, ct: ct);
                ok = Swap(b.Text, b.Symbols, epoch, wl.Commit());
                if (ok) _eventsLost = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Re-point/shutdown cancelled the reconcile rebuild. Leave the currently-installed index serving and
            // don't drain pending work - the context is being replaced (Init) or torn down.
            Log.For(Root).Info("reconcile canceled (re-point/shutdown)");
        }
        catch (Exception ex)
        {
            Rw.EnterWriteLock();
            try
            {
                // A transient compaction/reconcile failure (disk pressure, a locked segment) must NOT down a
                // healthy server. If a valid index is still installed it keeps serving - revert to Ready and keep
                // the captured edits so the next drain applies them. Only when no usable index exists do we fall
                // back to the CLI-build state (and only then is clearing pending correct).
                if (_text is not null && _symbols is not null) { _state = IndexState.Ready; keptServing = true; }
                else { _state = IndexState.NeedsCliBuild; _pendingPaths.Clear(); _pendingReconcile = false; _holdWantsRebuild = false; _heldPaths.Clear(); _heldBack = false; }
            }
            finally { Rw.ExitWriteLock(); }
            if (!keptServing) SetServing(false);
            else if (batch.FullReconcile)
            {
                // The rebuild was to catch the edits the watcher lost: they're still missing. Say so (as DrainPending does).
                _staleReason = "a rebuild of the index failed in this session and recent edits may be missing";
                _eventsLostUtc = DateTime.UtcNow;
                _eventsLost = true;
            }
            Log.For(Root).Error("reconcile/compaction failed" + (keptServing ? "; keeping the serving index" : "; falling back to CLI-build state"), ex);
            PublishStatus();
        }
        finally
        {
            if (rebuildingSet)
            {
                Rw.EnterWriteLock();
                try { EndRebuildingLocked(); } finally { Rw.ExitWriteLock(); }
            }
            BuildGate.Release();
        }
        if (ok || keptServing) DrainPending();
    }

    // Caller holds BuildGate, the cache's IndexWriteLock, and the Rw write lock (and RefreshIfStaleLocked returned true).
    // Returns whether a source the C# analyzer reads may have changed, and drops the analyzer if so (it rebuilds lazily).
    // retried: the paths among them that are only a retry of ones the share didn't answer for (see _unsurePaths).
    // untouched: nothing was applied or saved (see ChangeCounts.Untouched) - the caller publishes no new generation.
    private static bool ApplyIncremental(IReadOnlyCollection<string> changedFullPaths, out bool untouched,
                                         IReadOnlySet<string>? retried = null)
    {
        untouched = false;
        // A path naming a C# input counts before anything is applied, so the model goes even if indexing the edit fails. A
        // retry doesn't: that edit dropped the model when it arrived, and what the index finds now is CSharpInputsChanged.
        bool affects = RoslynCSharpAnalyzer.MayAffect(
            retried is not { Count: > 0 } ? changedFullPaths : changedFullPaths.Where(p => !retried.Contains(p)).ToList());
        try
        {
            _snapshot ??= RepositoryIndexer.LoadSnapshot(Root);
            var c = RepositoryIndexer.ApplyChanges(_text!, _symbols!, _snapshot, Root, changedFullPaths);
            untouched = c.Untouched;
            if (!untouched) RepositoryIndexer.Persist(Root, _text!, _symbols!, _snapshot);
            // ...and so does a C# file the index found added, changed or removed under a folder or a vanished path (a folder
            // renamed or deleted). An editor's vanished temp file, or a folder whose timestamp moved, changes nothing: a
            // README or .json edit leaves the ~15 s model valid.
            affects |= c.CSharpInputsChanged;
            if (c.Added != 0 || c.Modified != 0 || c.Removed != 0)
                Log.For(Root).Info($"incremental reindex: +{c.Added} ~{c.Modified} -{c.Removed} " +
                                   $"({changedFullPaths.Count} path(s) changed)");
            // The share didn't answer for these (see RepositoryIndexer.Probe): they stay as indexed - look again later. Said
            // once when it starts and once when it ends, not on every retry of a path that may never answer (access denied).
            bool wasUnsure = _unsurePaths.Count > 0 || retried is { Count: > 0 };
            _unsurePaths.ExceptWith(changedFullPaths);
            _unsurePaths.UnionWith(c.Unsure);
            if (_unsurePaths.Count > 0)
            {
                if (!wasUnsure)
                    Log.For(Root).Warn($"the share didn't answer for {c.Unsure.Count} changed path(s); kept as indexed, retrying");
                ScheduleRetry(unsure: true);
            }
            else
            {
                if (wasUnsure) Log.For(Root).Info("the share answered for every path it hadn't; the index caught up");
                _unsureDelayMs = 0;
            }
        }
        catch (Exception ex)
        {
            if (retried is not null) _unsurePaths.UnionWith(retried); // not looked at: still to retry
            affects = true; // the sources on disk changed whether or not the index caught up
            Log.For(Root).Error("incremental reindex failed", ex);
        }
        // An incomplete model (a file it couldn't read, a folder it couldn't list) is retried on a later edit.
        if (affects || IncompleteModelDueLocked()) { _csharp?.Dispose(); _csharp = null; }
        return affects;
    }

    // Edits held back from earlier batches (see HoldBackLocked) ride along with this one. Caller holds the Rw write lock and
    // is about to apply them, the index on disk having just loaded.
    private static IReadOnlyCollection<string> TakePendingLocked(IReadOnlyList<string> batch)
    {
        if (_heldBack) ClearHoldLocked();
        if (_pendingPaths.Count == 0) return batch;
        var all = new HashSet<string>(_pendingPaths, StringComparer.Ordinal);
        all.UnionWith(batch);
        _pendingPaths.Clear();
        return all;
    }

    // Apply changes captured during a build. Loops a bounded number of times to absorb edits
    // that land during the drain itself; anything after that the next watcher event will catch.
    private static void DrainPending()
    {
        var ct = ShutdownToken;
        int epoch = Volatile.Read(ref _epoch);
        for (int round = 0; round < 5; round++)
        {
            Rw.EnterReadLock();
            try { if (_pendingPaths.Count == 0 && !_pendingReconcile && !_unsureDue) return; }
            finally { Rw.ExitReadLock(); }

            // Same gate order as OnChanges: BuildGate -> IndexWriteLock -> Rw.
            try { BuildGate.Wait(ct); } catch (OperationCanceledException) { return; }
            bool rebuildingSet = false, reconcile = false;
            try
            {
                using var wl = IndexWriteLock.Acquire(CacheDir, ct);
                if (Volatile.Read(ref _epoch) != epoch) { wl.SkipBump(); return; }
                List<string> paths;
                bool affects = false, notReady = false, untouched = false;
                var failure = IndexLoadFailure.None;
                Rw.EnterWriteLock();
                try
                {
                    reconcile = _pendingReconcile;
                    bool holdCaused = _holdWantsRebuild;
                    _pendingReconcile = false;
                    _holdWantsRebuild = false; // taken: this round rebuilds
                    paths = _pendingPaths.ToList();
                    _pendingPaths.Clear();
                    // Paths the share didn't answer for, when their retry is due: once per firing of the timer, not every round.
                    HashSet<string>? retried = null;
                    if (_unsureDue)
                    {
                        _unsureDue = false;
                        retried = new HashSet<string>(_unsurePaths, StringComparer.Ordinal);
                        retried.ExceptWith(paths); // edited again meanwhile: a new edit, not a retry
                        _unsurePaths.Clear();
                        paths.AddRange(retried);
                    }
                    if (paths.Count == 0 && !reconcile) { wl.SkipBump(); return; }
                    // A rebuild only a hold asked for (the index on disk was missing or corrupt) isn't needed once the index
                    // loads again - fixed meanwhile by a terminal `codecompass index`, say: the held edits just apply.
                    // Only still missing or corrupt calls for it: briefly unreadable (a terminal build's fresh index, locked
                    // just now) is retried, never rebuilt over; a newer CodeCompass's index is left to it (see EnterNewerFormat).
                    var hold = reconcile && holdCaused && _state == IndexState.Ready ? RefreshIfStaleLocked() : IndexLoadFailure.Missing;
                    if (hold == IndexLoadFailure.None)
                    {
                        reconcile = false;
                        if (paths.Count == 0) { ClearHoldLocked(); wl.SkipBump(); return; }
                    }
                    if (hold is IndexLoadFailure.Transient or IndexLoadFailure.NewerFormat)
                    {
                        failure = hold;
                        reconcile = false;
                        if (hold == IndexLoadFailure.Transient)
                        {
                            _pendingReconcile = true;
                            _holdWantsRebuild = true;
                            _pendingPaths.UnionWith(paths);
                        }
                    }
                    else if (_state != IndexState.Ready && reconcile)
                    {
                        // Nothing loaded to reconcile yet: keep the request (and the edits) for when something is.
                        _pendingReconcile = true;
                        _pendingPaths.UnionWith(paths);
                        notReady = true;
                        reconcile = false;
                    }
                    else if (!reconcile)
                    {
                        // No index loaded to apply them to yet: keep them for when one is (see CaptureIfBusy).
                        if (_state != IndexState.Ready) { _pendingPaths.UnionWith(paths); notReady = true; }
                        else if ((failure = RefreshIfStaleLocked()) == IndexLoadFailure.None)
                        {
                            ClearHoldLocked();
                            affects = ApplyIncremental(paths, out untouched, retried);
                        }
                        else if (failure != IndexLoadFailure.NewerFormat) HoldBackLocked(paths, failure); // see OnChanges
                    }
                    // Divert the watcher to the pending queue during an off-lock reconcile Build, exactly as
                    // BackgroundReconcile does - an OnChanges arriving mid-Build is captured, drained next round.
                    if (reconcile) { _rebuilding++; rebuildingSet = true; }
                }
                finally { Rw.ExitWriteLock(); }

                if (notReady) { wl.SkipBump(); return; }
                if (failure != IndexLoadFailure.None)
                {
                    wl.SkipBump();
                    if (failure == IndexLoadFailure.NewerFormat) { EnterNewerFormat(); return; }
                    if (failure is IndexLoadFailure.Missing or IndexLoadFailure.Corrupt) continue; // the next round rebuilds it
                    ScheduleRetry();
                    return;
                }

                if (!reconcile)
                {
                    if (untouched) { wl.SkipBump(); continue; } // nothing written (see OnChanges)
                    if (!affects) wl.SourcesUnchanged();
                    _loadedGen = wl.Commit();
                    continue;
                }

                if (!ShouldAutoReconcile(Root))
                {
                    // Same gate as OnChanges: no in-session full rebuild of a network/huge root; disclose instead.
                    wl.SkipBump();
                    _staleReason = LostEventsReason;
                    _eventsLostUtc = DateTime.UtcNow;
                    _eventsLost = true;
                    EndHold(); // the lost-events note says it from here: they wait for a terminal update
                    Log.For(Root).Warn($"a captured full-reconcile request was not run in-session (network/large); run: codecompass update \"{Root}\"");
                    continue;
                }
                Log.For(Root).Info("draining a full-reconcile request captured during build");
                var b = RepositoryIndexer.Build(Root, ct: ct);
                if (Swap(b.Text, b.Symbols, epoch, wl.Commit())) { _eventsLost = false; EndHold(); }
            }
            catch (OperationCanceledException) { Log.For(Root).Info("drained update canceled (re-point/shutdown)"); return; }
            catch (Exception ex)
            {
                Log.For(Root).Error("drained update failed", ex);
                if (reconcile)
                {
                    // The rebuild failed (a full disk, an I/O error): the edits it was to cover aren't in the index. Say so
                    // on every query, with the way out, rather than a "retrying" note nothing would ever clear.
                    _staleReason = "a rebuild of the index failed in this session and recent edits may be missing";
                    _eventsLostUtc = DateTime.UtcNow;
                    _eventsLost = true;
                    EndHold();
                }
                return;
            }
            finally
            {
                BuildGate.Release();
                if (rebuildingSet)
                {
                    Rw.EnterWriteLock();
                    try { EndRebuildingLocked(); } finally { Rw.ExitWriteLock(); }
                }
            }
        }
    }
}
