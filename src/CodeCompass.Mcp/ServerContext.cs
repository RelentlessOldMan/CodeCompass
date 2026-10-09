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

    // Idle-eviction of the (large) semantic analyzers: a background timer drops them after a stretch
    // with no semantic query so a long session doesn't pin hundreds of MB / GB it's no longer using.
    private static long _lastSemanticUseMs;
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
    }
    private static List<LinkedRoot> _linked = new();

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

    private static string CacheDir => IndexStore.CacheDirPath(Root);
    private static bool _warnedNetworkCache;

    internal static bool IsLiveWatchOwnerForTest => _own is not null;
    internal static bool HasWatcherForTest { get { Rw.EnterReadLock(); try { return _watcher is not null; } finally { Rw.ExitReadLock(); } } }
    internal static void OnChangesForTest(ChangeBatch batch) => OnChanges(batch);
    internal static bool IsLinkedOwnerForTest(string root) => FindLinked(root)?.Own is not null;
    internal static bool LinkedHasWatcherForTest(string root) => FindLinked(root)?.Watcher is not null;
    private static LinkedRoot? FindLinked(string root) => _linked.FirstOrDefault(l => PathEq(l.Root, root));

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
            CodeCompassConfig.Load(Root); // per-repo .codecompass.json in effect for the size gate + build
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
            _rebuilding = false;
            _focus = null; // a focus names the PREVIOUS project's roots - stale after a re-point; start unscoped
            _loadedGen = null;
            _eventsLost = false;
            _statusOverride = null;
            _epoch++; // re-point: any in-flight build for the previous root is now stale (discarded at Swap)
        }
        finally { Rw.ExitWriteLock(); }
        oldWatcher?.Dispose();
        oldCs?.Dispose();
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
        DisposeLinkedRoots();
        WriteOwnership? own;
        lock (_ownGate) { own = _own; _own = null; }
        own?.Dispose();
        Interlocked.Exchange(ref _liveness, null)?.Dispose();
    }

    // This process's "serving Root" marker for the Grep hook (see ServerLiveness).
    private static ServerLiveness? _liveness;

    // A read-only session takes over the live-watch role once its holder exits (the cheap probe is a failed exclusive
    // open of a local file). It then starts the watcher it was asked for, and reconciles once to catch the edits made
    // while nobody was watching.
    private static void MaybePromoteToOwner()
    {
        if (_own is not null || string.IsNullOrEmpty(Root)) return;
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
            if (_state != IndexState.Ready || _rebuilding) return;
            gen = IndexGeneration.Read(CacheDir); // read BEFORE loading: a write that lands mid-load just reloads again
            if (gen is null || gen == _loadedGen) return;
            int epoch = Volatile.Read(ref _epoch);
            if (!RepositoryIndexer.TryLoad(Root, out var t, out var s)) return;
            if (Swap(t, s, epoch, gen, sourcesChanged: SourcesMoved(Root, gen)))
            {
                Log.For(Root).Info("reloaded the index: another CodeCompass process updated it");
                ClearEventsLostIfRefreshed();
            }
        }
        finally { BuildGate.Release(); }
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
            foreach (var lr in _linked)
            {
                if (lr.Own is not null) continue;
                var won = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(lr.Root));
                if (won is null) continue; // still owned elsewhere
                lr.Own = won;
                var linkedRoot = lr.Root;
                lr.Watcher = new RepositoryWatcher(linkedRoot, b => OnLinkedChanges(linkedRoot, b), 1000); // off Rw, as in ReconcileTo
                lr.Watcher.Start();
                Log.For(linkedRoot).Info("took over live indexing for this linked root (the session that held it ended)");
                Task.Run(() => ReconcileLinkedOnLoad(linkedRoot));
            }
        }
        finally { Monitor.Exit(_linkedGate); }
    }

    // Same freshness rule for linked roots: whoever writes a linked root (its owner here, another session, or the
    // CLI), every session federating it reloads on the next query.
    private static void MaybeReloadLinked()
    {
        var linked = _linked;
        foreach (var lr in linked)
        {
            var gen = IndexGeneration.Read(IndexStore.CacheDirPath(lr.Root));
            if (gen is null || gen == lr.Gen) continue;
            if (!BuildGate.Wait(0)) return;
            try
            {
                gen = IndexGeneration.Read(IndexStore.CacheDirPath(lr.Root));
                if (gen is null || gen == lr.Gen) continue;
                if (RepositoryIndexer.TryLoad(lr.Root, out var t, out var s))
                    SwapLinked(lr.Root, t, s, gen, sourcesChanged: SourcesMoved(lr.Root, gen));
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

    // Appended to every query result while _eventsLost is set (see there).
    private static string EventsLostNote() => _eventsLost
        ? $"\n(Note: the file watcher lost change events on this network/large workspace and an automatic full " +
          $"rebuild is disabled here, so results may be STALE. Refresh from a terminal: codecompass update \"{Root}\")"
        : "";

    // Caller holds BuildGate + the cache's IndexWriteLock + the Rw write lock. If another process committed since the
    // installed index was loaded, reload it before mutating - a stale manifest flushed over the newer one would drop
    // that writer's segments from the index.
    private static void RefreshIfStaleLocked()
    {
        var gen = IndexGeneration.Read(CacheDir);
        if (gen is not null && gen == _loadedGen) return;
        if (!RepositoryIndexer.TryLoad(Root, out var t, out var s))
        {
            Log.For(Root).Warn("the index changed on disk but could not be reloaded; applying edits to the loaded copy");
            return;
        }
        _text?.Dispose(); _symbols?.Dispose(); _snapshot?.Dispose();
        _text = t; _symbols = s; _snapshot = null;
        if (SourcesMoved(Root, gen)) { _csharp?.Dispose(); _csharp = null; }
        _loadedGen = gen;
        Log.For(Root).Info("reloaded the index before applying edits (another CodeCompass process had updated it)");
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
        foreach (var lr in remove) { lr.Text.Dispose(); lr.Symbols.Dispose(); lr.Own?.Dispose(); }

        foreach (var lr in added) // owners live-watch (off-lock) + reconcile out-of-session changes (gated)
        {
            if (lr.Own is null) continue;
            var linkedRoot = lr.Root;
            lr.Watcher = new RepositoryWatcher(linkedRoot, b => OnLinkedChanges(linkedRoot, b), 1000);
            lr.Watcher.Start();
            Task.Run(() => ReconcileLinkedOnLoad(linkedRoot));
        }

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
        try { BuildGate.Wait(ct); } catch (OperationCanceledException) { return; }
        SegmentedIndex? nt = null;
        SegmentedSymbolIndex? ns = null;
        try
        {
            using var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(linkedRoot), ct);
            if (batch.FullReconcile)
            {
                // Lost events on a network/huge linked root: a full in-session rebuild is gated off, same as the
                // project root (see OnChanges). A terminal `codecompass update` refreshes it; we reload on its commit.
                if (!ShouldAutoReconcile(linkedRoot))
                {
                    wl.SkipBump();
                    Log.For(linkedRoot).Warn("watcher lost events on this linked root; skipped an in-session full rebuild " +
                                             $"(network/large) - run: codecompass update \"{linkedRoot}\"");
                    return;
                }
                var b = RepositoryIndexer.Build(linkedRoot, ct: ct); nt = b.Text; ns = b.Symbols;
            }
            else { var u = RepositoryIndexer.UpdatePaths(linkedRoot, batch.ChangedFullPaths, ct); nt = u.Text; ns = u.Symbols; }
            bool affects = batch.FullReconcile || RoslynCSharpAnalyzer.MayAffect(batch.ChangedFullPaths);
            if (!affects) wl.SourcesUnchanged();
            SwapLinked(linkedRoot, nt, ns, wl.Commit(), sourcesChanged: affects);
            nt = null; ns = null; // ownership transferred to _linked
        }
        catch (OperationCanceledException) { Log.For(linkedRoot).Info("linked root update canceled (re-point/shutdown)"); }
        catch (Exception ex) { Log.For(linkedRoot).Error("linked root live update failed", ex); }
        finally { nt?.Dispose(); ns?.Dispose(); BuildGate.Release(); }
    }

    // Install fresh indexes for a linked root under the write lock (so no in-flight federated read touches
    // a disposed mmap), disposing the old ones. If the root was unlinked meanwhile, the new ones are dropped.
    // sourcesChanged false (a reconcile or edit that can't have changed what the C# analyzer read) keeps it - see Swap.
    private static void SwapLinked(string linkedRoot, SegmentedIndex text, SegmentedSymbolIndex symbols, string? gen,
                                   bool sourcesChanged = true)
    {
        SegmentedIndex? oldT = null; SegmentedSymbolIndex? oldS = null; bool installed = false;
        Rw.EnterWriteLock();
        try
        {
            var lr = _linked.FirstOrDefault(l => PathEq(l.Root, linkedRoot));
            if (lr is not null) { oldT = lr.Text; oldS = lr.Symbols; lr.Text = text; lr.Symbols = symbols; lr.Gen = gen; installed = true; }
        }
        finally { Rw.ExitWriteLock(); }
        oldT?.Dispose(); oldS?.Dispose();
        if (!installed) { text.Dispose(); symbols.Dispose(); } // unlinked while updating
        // The C# analyzer spans this linked root too, so if its sources changed, drop the cached model so the next
        // semantic query rebuilds over the fresh sources (lazy, off the query path).
        if (sourcesChanged || SourcesMoved(linkedRoot, gen)) InvalidateSemanticAnalyzers(); // see Swap
    }

    // Reconcile a linked root against its current tree on load (out-of-session changes), gated exactly
    // like the project root: local + within the auto limit -> reconcile now; network/huge -> deferred to
    // a manual `codecompass update "<root>"` (a full stat-walk of a huge/network tree is too slow to auto-run).
    private static void ReconcileLinkedOnLoad(string linkedRoot)
    {
        try
        {
            // ShouldAutoReconcile reads the linked root's own config via ReadFrom (not the ambient _current),
            // and Update() reloads config for its root internally under BuildGate - so no Load() here, which
            // would race the project root's ambient config from this background thread.
            if (!ShouldAutoReconcile(linkedRoot)) return;
            ReconcileLinked(linkedRoot);
        }
        catch (OperationCanceledException) { Log.For(linkedRoot).Info("linked root reconcile canceled (re-point/shutdown)"); }
        catch (Exception ex) { Log.For(linkedRoot).Warn($"linked root reconcile skipped: {ex.Message}"); }
    }

    private static void ReconcileLinked(string linkedRoot)
    {
        var ct = ShutdownToken;
        BuildGate.Wait(ct);
        try
        {
            using var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(linkedRoot), ct);
            var u = RepositoryIndexer.Update(linkedRoot, ct: ct);
            bool changed = u.Stats.Added != 0 || u.Stats.Modified != 0 || u.Stats.Removed != 0 || u.Stats.FullRebuild;
            if (!changed) wl.SourcesUnchanged();
            SwapLinked(linkedRoot, u.Text, u.Symbols, wl.Commit(), sourcesChanged: changed);
        }
        finally { BuildGate.Release(); }
    }

    /// <summary>Test seam: run a linked root's startup reconcile synchronously, past its auto-reconcile gate.</summary>
    internal static void RunLinkedStartupReconcileNow(string linkedRoot) => ReconcileLinked(PathSafety.NormalizeDir(linkedRoot));

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
            string focusNote = FocusDisclosure(all.Count, handles) + EventsLostNote() + StalenessNote(handles);

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

    // Stamp semantic use and make sure the eviction timer is running (armed once, on first semantic use).
    private static void TouchSemantic()
    {
        Volatile.Write(ref _lastSemanticUseMs, Environment.TickCount64);
        if (_evictTimer is not null) return;
        int idleMin = CodeCompassConfig.SemanticIdleMinutes();
        if (idleMin <= 0) return; // eviction disabled -> keep resident
        long periodMs = Math.Clamp(idleMin * 60_000L / 4, 15_000L, 60_000L); // check a few times per window
        _evictTimer = new Timer(_ => EvictIdleAnalyzers(), null, periodMs, periodMs);
    }

    // Drop the semantic analyzers if they've gone unused past the idle window, freeing their (large)
    // in-memory models. Takes the write lock so it can't dispose an analyzer mid-query (find_references
    // holds the read lock for its whole duration); the analyzers rebuild lazily on the next access.
    private static void EvictIdleAnalyzers()
    {
        int idleMin = CodeCompassConfig.SemanticIdleMinutes();
        if (idleMin <= 0) return;
        long idleMs = idleMin * 60_000L;
        if (Environment.TickCount64 - Volatile.Read(ref _lastSemanticUseMs) < idleMs) return;
        if (_csharp is null) return;

        RoslynCSharpAnalyzer? cs;
        Rw.EnterWriteLock();
        try
        {
            if (Environment.TickCount64 - Volatile.Read(ref _lastSemanticUseMs) < idleMs) return; // used just now
            cs = _csharp;
            if (cs is null) return;
            _csharp = null;
        }
        finally { Rw.ExitWriteLock(); }
        cs?.Dispose(); // outside the lock; they're detached and unreachable now
        Log.For(Root).Info($"evicted idle semantic analyzer(s) after ~{idleMin} min unused to free memory");
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
        try { _rebuilding = true; } // divert the watcher to the pending queue; waits out any in-flight incremental
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
            try { _rebuilding = false; }
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
                // changed nothing itself carries the current token, which may be another writer's newer one.
                if (sourcesChanged || SourcesMoved(Root, gen))
                {
                    oldCs = _csharp; // stale after a rebuild; dispose off-lock to free their model
                    _csharp = null;
                }
                _state = IndexState.Ready;
                _loadedGen = gen;
                _statusOverride = null;
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
        Rw.EnterWriteLock();
        try
        {
            if (_watcher is not null) return;
            _watcher = new RepositoryWatcher(Root, OnChanges, _liveDebounceMs);
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
                _statusOverride = "This workspace's index was written by a NEWER CodeCompass than this one, so it can't be read " +
                                  "here (and won't be rebuilt over). Upgrade this CodeCompass install - or, to rebuild it for this " +
                                  $"version deliberately: codecompass index \"{Root}\"";
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
            if (RepositoryIndexer.TryLoad(Root, out var existing, out var existingSymbols))
            {
                wl.SkipBump();
                ok = Swap(existing, existingSymbols, epoch, IndexGeneration.Read(CacheDir));
                if (ok) Log.For(Root).Info("loaded the index another CodeCompass process built while this one waited");
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
        try
        {
            if (_own is null) return; // the live-watch role holder reconciles; a read-only session reloads its result
            if (ShouldAutoReconcile()) { BackgroundReconcile(); return; }
            // The full stat-walk reconcile is gated off (network share or huge repo). Unless the user
            // explicitly disabled auto-reconcile, still run the CHEAP ignore-only prune: a stale index (built
            // by an older/looser version) can hold now-ignored pollution (a rival tool's .claude cache dump, a
            // dir since added to CODECOMPASS_IGNORE), and the query-time filter only HIDES it - this physically
            // drops it, with no re-walk, so even network/huge repos self-heal on startup.
            if (CodeCompassConfig.AutoReconcile() != false) BackgroundPruneIgnored();
        }
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

    // Same gate for a linked root, reading THAT root's own config (not the ambient project config, since
    // this runs on a background thread) so a network/huge linked root is likewise left to a manual update.
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
        try { _rebuilding = true; _reconciling = true; }
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
            try { _rebuilding = false; _reconciling = false; }
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
        try { _rebuilding = true; }
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
            try { _rebuilding = false; }
            finally { Rw.ExitWriteLock(); }
            DrainPending();
        }
    }

    // The auto-index size policy lives in Core (RepositoryIndexer.ExceedsAutoLimit) so the project root
    // (here) and linked roots (`link add`) apply the identical "small -> index, large -> defer" rule.
    private static bool ExceedsAutoLimit(out long totalBytes) => RepositoryIndexer.ExceedsAutoLimit(Root, out totalBytes);

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

    // Capture a watcher batch into the pending queue when a build/rebuild owns the cache (state Building, or a
    // rebuild/reconcile in flight), so the edit isn't lost - the owner drains it when it finishes. Returns true if
    // captured. A rebuild sets its flag BEFORE queuing on BuildGate, so this is also re-checked after the gates.
    private static bool CaptureIfBusy(ChangeBatch batch)
    {
        Rw.EnterUpgradeableReadLock();
        try
        {
            if (_state != IndexState.Building && !_rebuilding) return false;
            Rw.EnterWriteLock();
            try
            {
                if (batch.FullReconcile) _pendingReconcile = true;
                else _pendingPaths.AddRange(batch.ChangedFullPaths);
            }
            finally { Rw.ExitWriteLock(); }
            return true;
        }
        finally { Rw.ExitUpgradeableReadLock(); }
    }

    private static void OnChanges(ChangeBatch batch)
    {
        // Capture the teardown token and epoch BEFORE any wait: a re-point re-arms ShutdownToken and bumps the epoch,
        // so values read after waiting would belong to the NEW context and let this stale callback run to completion
        // (blocking the watcher's Dispose barrier, and building the new root's tree from an old root's event).
        var ct = ShutdownToken;
        int epoch = Volatile.Read(ref _epoch);
        if (CaptureIfBusy(batch)) return;
        if (_state != IndexState.Ready) return;

        // Every write takes the gates in ONE order: BuildGate (this process's builders) -> IndexWriteLock (all
        // processes) -> Rw. Both waits are cancellable, and neither holds Rw, so a long write by another process (a
        // terminal `codecompass index`) only parks this callback - queries keep serving the loaded index meanwhile.
        try { BuildGate.Wait(ct); } catch (OperationCanceledException) { return; }
        bool ok = false, keptServing = false;
        try
        {
            using var wl = IndexWriteLock.Acquire(CacheDir, ct);
            if (ct.IsCancellationRequested || Volatile.Read(ref _epoch) != epoch) { wl.SkipBump(); return; }
            if (CaptureIfBusy(batch) || _state != IndexState.Ready) { wl.SkipBump(); return; }

            if (!batch.FullReconcile)
            {
                // Targeted incremental: fast, done under the write lock. If segments have piled up, escalate to a
                // compaction instead of just returning.
                bool compact;
                bool affects = RoslynCSharpAnalyzer.MayAffect(batch.ChangedFullPaths);
                Rw.EnterWriteLock();
                try
                {
                    RefreshIfStaleLocked(); // another process wrote since we loaded: mutate THEIR state, not our stale copy
                    ApplyIncremental(batch.ChangedFullPaths, batch.ChangedFullPaths.Count);
                    compact = RepositoryIndexer.NeedsCompaction(_text!, _symbols!);
                    if (compact) _state = IndexState.Building;
                }
                finally { Rw.ExitWriteLock(); }
                if (!affects) wl.SourcesUnchanged(); // other sessions reload the index but keep their C# model
                _loadedGen = wl.Commit();
                if (!compact) return;
                Log.For(Root).Info("compacting: segment count high after incremental edits; merging segments");
                var c = RepositoryIndexer.Compact(Root, ct);
                if (!affects) wl.SourcesUnchanged(); // Compact's nested hold reset it; a merge changes no source
                ok = Swap(c.Text, c.Symbols, epoch, wl.Commit(), sourcesChanged: false); // a merge of segments; no source changed
            }
            else
            {
                // Events were lost (watcher buffer overflow). Only a full re-walk can be trusted - but on a network share
                // or a repo over the auto-index limit that is an hours-long rebuild, the very work the startup reconcile
                // and the reindex tool refuse to do in-session. Keep serving, and say on every query that results may
                // be stale until a terminal `codecompass update` (whose commit this session then reloads).
                if (!ShouldAutoReconcile())
                {
                    wl.SkipBump();
                    _eventsLostUtc = DateTime.UtcNow;
                    _eventsLost = true;
                    Log.For(Root).Warn("watcher lost change events; skipped an in-session full rebuild on this network/large " +
                                       $"workspace - results may be stale until: codecompass update \"{Root}\"");
                    return;
                }
                Rw.EnterWriteLock();
                try { _state = IndexState.Building; }
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
                else { _state = IndexState.NeedsCliBuild; _pendingPaths.Clear(); _pendingReconcile = false; }
            }
            finally { Rw.ExitWriteLock(); }
            Log.For(Root).Error("reconcile/compaction failed" + (keptServing ? "; keeping the serving index" : "; falling back to CLI-build state"), ex);
            PublishStatus();
        }
        finally { BuildGate.Release(); }
        if (ok || keptServing) DrainPending();
    }

    // Caller holds BuildGate, the cache's IndexWriteLock, and the Rw write lock (and has run RefreshIfStaleLocked).
    private static void ApplyIncremental(IReadOnlyList<string> changedFullPaths, int changedCount)
    {
        try
        {
            _snapshot ??= RepositoryIndexer.LoadSnapshot(Root);
            var c = RepositoryIndexer.ApplyChanges(_text!, _symbols!, _snapshot, Root, changedFullPaths);
            RepositoryIndexer.Persist(Root, _text!, _symbols!, _snapshot);
            // Stale only if the edit touched what it read (a README or .json edit leaves a ~15 s model valid). Rebuilds lazily.
            if (RoslynCSharpAnalyzer.MayAffect(changedFullPaths)) { _csharp?.Dispose(); _csharp = null; }
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
        var ct = ShutdownToken;
        int epoch = Volatile.Read(ref _epoch);
        for (int round = 0; round < 5; round++)
        {
            Rw.EnterReadLock();
            try { if (_pendingPaths.Count == 0 && !_pendingReconcile) return; }
            finally { Rw.ExitReadLock(); }

            // Same gate order as OnChanges: BuildGate -> IndexWriteLock -> Rw.
            try { BuildGate.Wait(ct); } catch (OperationCanceledException) { return; }
            bool rebuildingSet = false;
            try
            {
                using var wl = IndexWriteLock.Acquire(CacheDir, ct);
                if (Volatile.Read(ref _epoch) != epoch) { wl.SkipBump(); return; }
                List<string> paths;
                bool reconcile;
                Rw.EnterWriteLock();
                try
                {
                    reconcile = _pendingReconcile;
                    _pendingReconcile = false;
                    paths = _pendingPaths.ToList();
                    _pendingPaths.Clear();
                    if (paths.Count == 0 && !reconcile) { wl.SkipBump(); return; }
                    if (!reconcile && _state == IndexState.Ready)
                    {
                        RefreshIfStaleLocked();
                        ApplyIncremental(paths, paths.Count);
                    }
                    // Divert the watcher to the pending queue during an off-lock reconcile Build, exactly as
                    // BackgroundReconcile does - an OnChanges arriving mid-Build is captured, drained next round.
                    if (reconcile) { _rebuilding = true; rebuildingSet = true; }
                }
                finally { Rw.ExitWriteLock(); }

                if (!reconcile)
                {
                    if (!RoslynCSharpAnalyzer.MayAffect(paths)) wl.SourcesUnchanged();
                    _loadedGen = wl.Commit();
                    continue;
                }

                if (!ShouldAutoReconcile())
                {
                    // Same gate as OnChanges: no in-session full rebuild of a network/huge root; disclose instead.
                    wl.SkipBump();
                    _eventsLostUtc = DateTime.UtcNow;
                    _eventsLost = true;
                    Log.For(Root).Warn($"a captured full-reconcile request was not run in-session (network/large); run: codecompass update \"{Root}\"");
                    continue;
                }
                Log.For(Root).Info("draining a full-reconcile request captured during build");
                var b = RepositoryIndexer.Build(Root, ct: ct);
                if (Swap(b.Text, b.Symbols, epoch, wl.Commit())) _eventsLost = false;
            }
            catch (OperationCanceledException) { Log.For(Root).Info("drained update canceled (re-point/shutdown)"); return; }
            catch (Exception ex) { Log.For(Root).Error("drained update failed", ex); return; }
            finally
            {
                BuildGate.Release();
                if (rebuildingSet)
                {
                    Rw.EnterWriteLock();
                    try { _rebuilding = false; } finally { Rw.ExitWriteLock(); }
                }
            }
        }
    }
}
