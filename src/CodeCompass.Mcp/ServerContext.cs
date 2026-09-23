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
    }
    private static List<LinkedRoot> _linked = new();

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

    public static void Init(string root)
    {
        RepositoryWatcher? oldWatcher;
        RoslynCSharpAnalyzer? oldCs;
        ClangCppAnalyzer? oldCpp;
        Rw.EnterWriteLock();
        try
        {
            oldWatcher = _watcher; // dispose after releasing the lock (see StopLiveIndex)
            _watcher = null;
            oldCs = _csharp; oldCpp = _cpp; // ditto - dispose off-lock
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
        oldCs?.Dispose(); oldCpp?.Dispose();
        // Off-lock: tearing down linked roots disposes their watchers, which must never happen under Rw
        // (a watcher.Dispose drains an in-flight OnLinkedChanges -> SwapLinked that itself takes Rw).
        DisposeLinkedRoots();
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
            try { norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw)); } catch { continue; } // skip a bad path
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
                if (!RepositoryIndexer.TryLoad(linkedRoot, out var t, out var s)) continue; // not indexed yet
                var own = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(linkedRoot));
                added.Add(new LinkedRoot { Root = linkedRoot, Text = t, Symbols = s, Own = own });
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
    private static bool PathEq(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    // A linked root we own: a debounced batch of edits -> update THAT root's own index on disk (its own
    // cache/snapshot), then swap the federated handle. Mirrors the project's OnChanges but simpler - a
    // linked root has no compaction-escalation state machine here. Serialized against all builds by BuildGate.
    private static void OnLinkedChanges(string linkedRoot, ChangeBatch batch)
    {
        BuildGate.Wait();
        SegmentedIndex? nt = null;
        SegmentedSymbolIndex? ns = null;
        try
        {
            if (batch.FullReconcile) { var b = RepositoryIndexer.Build(linkedRoot); nt = b.Text; ns = b.Symbols; }
            else { var u = RepositoryIndexer.UpdatePaths(linkedRoot, batch.ChangedFullPaths); nt = u.Text; ns = u.Symbols; }
            SwapLinked(linkedRoot, nt, ns);
            nt = null; ns = null; // ownership transferred to _linked
        }
        catch (Exception ex) { Log.For(linkedRoot).Error("linked root live update failed", ex); }
        finally { nt?.Dispose(); ns?.Dispose(); BuildGate.Release(); }
    }

    // Install fresh indexes for a linked root under the write lock (so no in-flight federated read touches
    // a disposed mmap), disposing the old ones. If the root was unlinked meanwhile, the new ones are dropped.
    private static void SwapLinked(string linkedRoot, SegmentedIndex text, SegmentedSymbolIndex symbols)
    {
        SegmentedIndex? oldT = null; SegmentedSymbolIndex? oldS = null; bool installed = false;
        Rw.EnterWriteLock();
        try
        {
            var lr = _linked.FirstOrDefault(l => PathEq(l.Root, linkedRoot));
            if (lr is not null) { oldT = lr.Text; oldS = lr.Symbols; lr.Text = text; lr.Symbols = symbols; installed = true; }
        }
        finally { Rw.ExitWriteLock(); }
        oldT?.Dispose(); oldS?.Dispose();
        if (!installed) { text.Dispose(); symbols.Dispose(); } // unlinked while updating
        // The C#/C++ analyzers span this linked root too, so its content just changed under them - drop the
        // cached model so the next semantic query rebuilds over the fresh sources (lazy, off the query path).
        InvalidateSemanticAnalyzers();
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
            BuildGate.Wait();
            try { var u = RepositoryIndexer.Update(linkedRoot); SwapLinked(linkedRoot, u.Text, u.Symbols); }
            finally { BuildGate.Release(); }
        }
        catch (Exception ex) { Log.For(linkedRoot).Warn($"linked root reconcile skipped: {ex.Message}"); }
    }

    /// <summary>
    /// Federated read: run <paramref name="op"/> against the primary index plus every loaded linked
    /// index, under the read lock. The op merges results itself (linked hits are shown with absolute
    /// paths - see the tools). Returns the human-readable status if the primary index isn't ready.
    /// </summary>
    internal static string QueryAll(Func<IReadOnlyList<IndexHandle>, string> op)
    {
        EnsureStartedLocked();
        MaybeReconcileLinks(); // cheap stat; picks up a terminal `link add`/`remove` and guarantees the query sees the current set
        Rw.EnterReadLock();
        try
        {
            if (_state != IndexState.Ready || _text is null || _symbols is null) return StatusMessage();
            var handles = new List<IndexHandle>(1 + _linked.Count) { new(Root, _text, _symbols, IsPrimary: true) };
            foreach (var lr in _linked) handles.Add(new IndexHandle(lr.Root, lr.Text, lr.Symbols, IsPrimary: false));
            return op(handles);
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
        get { lock (AnalyzerGate) { TouchSemantic(); return _csharp ??= new RoslynCSharpAnalyzer(AllRootsSnapshot()); } }
    }

    public static ClangCppAnalyzer Cpp
    {
        get { lock (AnalyzerGate) { TouchSemantic(); return _cpp ??= new ClangCppAnalyzer(AllRootsSnapshot()); } }
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
        ClangCppAnalyzer? cpp;
        Rw.EnterWriteLock();
        try { cs = _csharp; cpp = _cpp; _csharp = null; _cpp = null; }
        finally { Rw.ExitWriteLock(); }
        cs?.Dispose(); cpp?.Dispose();
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
        if (_csharp is null && _cpp is null) return;

        RoslynCSharpAnalyzer? cs;
        ClangCppAnalyzer? cpp;
        Rw.EnterWriteLock();
        try
        {
            if (Environment.TickCount64 - Volatile.Read(ref _lastSemanticUseMs) < idleMs) return; // used just now
            cs = _csharp; cpp = _cpp;
            if (cs is null && cpp is null) return;
            _csharp = null; _cpp = null;
        }
        finally { Rw.ExitWriteLock(); }
        cs?.Dispose(); cpp?.Dispose(); // outside the lock; they're detached and unreachable now
        Log.For(Root).Info($"evicted idle semantic analyzer(s) after ~{idleMin} min unused to free memory");
    }

    /// <summary>Test seam: are the semantic analyzers currently resident?</summary>
    internal static bool HasResidentSemanticAnalyzers()
    {
        lock (AnalyzerGate) return _csharp is not null || _cpp is not null;
    }

    /// <summary>Test seam: force the idle-eviction path now, regardless of the idle window.</summary>
    internal static void EvictSemanticAnalyzersNow()
    {
        RoslynCSharpAnalyzer? cs;
        ClangCppAnalyzer? cpp;
        Rw.EnterWriteLock();
        try { cs = _csharp; cpp = _cpp; _csharp = null; _cpp = null; }
        finally { Rw.ExitWriteLock(); }
        cs?.Dispose(); cpp?.Dispose();
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
            // reindex is the manual lever to FORCE a linked-root re-probe: a root that was linked-but-unindexed
            // (large, deferred to `codecompass index`) and has since been built isn't picked up by the signature
            // watch (links.json didn't change), so clear the flag to re-attempt loading it. The safe-diff only
            // retries the not-yet-loaded roots; already-federated ones are untouched.
            lock (_linkedGate) { _linksChecked = false; }
            Task.Run(MaybeReconcileLinks);
        }
    }

    // Dispose the previous indexes and install new ones under the write lock (waits for any
    // in-flight search to finish, so a search never touches a disposed mmap).
    private static void Swap(SegmentedIndex text, SegmentedSymbolIndex symbols)
    {
        RoslynCSharpAnalyzer? oldCs;
        ClangCppAnalyzer? oldCpp;
        Rw.EnterWriteLock();
        try
        {
            _text?.Dispose();
            _symbols?.Dispose();
            _snapshot?.Dispose();
            oldCs = _csharp; oldCpp = _cpp; // stale after a rebuild; dispose off-lock to free their model
            _text = text;
            _symbols = symbols;
            _snapshot = null;
            _csharp = null;
            _cpp = null;
            _state = IndexState.Ready;
        }
        finally { Rw.ExitWriteLock(); }
        oldCs?.Dispose(); oldCpp?.Dispose();
        // meta.json (path/version/coverage) is written by RepositoryIndexer.Build/Update, which have the
        // walker's over-cap count; Swap must not overwrite it here (it has no coverage data).
        PublishStatus(); // now Ready (or reconciling, if a startup reconcile is still in flight)
        Task.Run(MaybeReconcileLinks); // warm the federated set off-lock (cheap no-op if unchanged)
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
                Task.Run(MaybeReconcileLinks); // warm the federated set off-lock (own watchers/reconcile)
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

    // The auto-index size policy lives in Core (RepositoryIndexer.ExceedsAutoLimit) so the project root
    // (here) and linked roots (`link add`) apply the identical "small -> index, large -> defer" rule.
    private static bool ExceedsAutoLimit(out long totalBytes) => RepositoryIndexer.ExceedsAutoLimit(Root, out totalBytes);

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
            _csharp?.Dispose(); _csharp = null; // stale after an edit; free the model, rebuilds lazily
            _cpp?.Dispose(); _cpp = null;
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
