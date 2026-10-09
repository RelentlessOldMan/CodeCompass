namespace CodeCompass.Core.Storage;

/// <summary>
/// Cross-process mutual exclusion for ONE write operation on an index cache dir (a build, an update, an
/// incremental apply+persist, a compaction). Every mutation of a cache - from any MCP session, the CLI
/// <c>index</c>/<c>update</c>/<c>watch</c> commands, or a linked-root owner - holds this for its duration, so two
/// processes can never interleave segment numbering, manifest replaces, or orphan cleanup in one directory.
///
/// Like <see cref="WriteOwnership"/> it is an OS-held EXCLUSIVE file handle (crash/kill/power-loss safe: the
/// kernel drops the claim with the process; a leftover lock FILE is inert). Unlike WriteOwnership (a long-lived
/// "who live-watches this cache" role) it is held only while writing, so a terminal <c>codecompass index</c>
/// can always run while a session is open - it just waits for an in-flight write to finish.
///
/// Releasing a lock bumps the cache's <see cref="IndexGeneration"/> token. A process holding the index in
/// memory compares that token to the one it loaded; a mismatch means another process wrote, so it must reload
/// before mutating (a stale in-memory manifest flushed over a newer one would orphan the other writer's
/// segments) and should reload to keep serving fresh results.
///
/// Re-entrant per THREAD: a write op that calls another locked op on the same thread (Update falling back to
/// Build, a caller wrapping RepositoryIndexer.Build to learn the committed generation) nests instead of
/// deadlocking on its own handle. Different threads in one process exclude each other exactly like processes.
/// </summary>
public sealed class IndexWriteLock : IDisposable
{
    private const string Name = ".write.lock";
    private const int PollMs = 100;

    [ThreadStatic] private static Dictionary<string, IndexWriteLock>? t_held;

    /// <summary>Optional sink for a one-line "waiting for another writer" notice (the CLI points it at stderr so
    /// a blocked <c>codecompass index</c> doesn't look hung). Called at most once per <see cref="Acquire"/>.</summary>
    public static Action<string>? WaitNotice;

    private readonly string _dir;
    private readonly string _key;
    private FileStream? _handle;
    private int _depth;
    private bool _committed;

    private IndexWriteLock(string dir, string key, FileStream handle)
    {
        _dir = dir; _key = key; _handle = handle; _depth = 1;
    }

    /// <summary>Try to take the write lock without waiting. Null if another process (or thread) is writing.</summary>
    public static IndexWriteLock? TryAcquire(string cacheDir)
    {
        var key = Key(cacheDir);
        var held = t_held ??= new Dictionary<string, IndexWriteLock>(StringComparer.OrdinalIgnoreCase);
        // A nested op is about to write more, so whatever was committed before it is no longer the latest state.
        if (held.TryGetValue(key, out var mine)) { mine._depth++; mine._committed = false; mine._lastToken = null; mine._sourcesUnchanged = false; return mine; }
        var fs = TryOpen(cacheDir);
        if (fs is null) return null;
        var lk = new IndexWriteLock(cacheDir, key, fs);
        held[key] = lk;
        return lk;
    }

    /// <summary>Take the write lock, waiting for any in-flight writer to finish. Cancellable (a re-point or
    /// shutdown abandons the wait); never times out on its own - waiting on a long terminal build is correct.</summary>
    public static IndexWriteLock Acquire(string cacheDir, CancellationToken ct = default)
    {
        bool noticed = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var lk = TryAcquire(cacheDir);
            if (lk is not null) return lk;
            if (!noticed)
            {
                noticed = true;
                try { WaitNotice?.Invoke("waiting for another CodeCompass process to finish writing this index..."); } catch { }
            }
            if (ct.WaitHandle.WaitOne(PollMs)) ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Is the write lock for <paramref name="cacheDir"/> held by the current thread?</summary>
    public static bool HeldByCurrentThread(string cacheDir) =>
        t_held is { } h && h.ContainsKey(Key(cacheDir));

    /// <summary>Publish this op's writes: bump the generation token and return it. The caller records the token
    /// as "the generation my in-memory index reflects". Idempotent within one hold (returns the same token).</summary>
    public string Commit()
    {
        if (_handle is null) throw new ObjectDisposedException(nameof(IndexWriteLock));
        if (_committed && _lastToken is not null) return _lastToken;
        _lastToken = IndexGeneration.Bump(_dir, carrySources: _sourcesUnchanged);
        _committed = true;
        return _lastToken;
    }
    private string? _lastToken;

    /// <summary>This hold wrote nothing (it took the lock, then found there was nothing to do): release without
    /// bumping the generation, so other processes don't reload an unchanged index. Only valid if NOTHING was
    /// written under this hold - when in doubt, let the release bump (a spurious reload is safe; a missed one isn't).</summary>
    public void SkipBump() { _committed = true; _lastToken = null; }

    /// <summary>This hold's writes changed no source a semantic model reads (a reconcile that found nothing, an edit to a
    /// README): the commit carries the previous sources token forward, so other processes reload the index but keep
    /// their resident C# model. Without this call every commit moves it - the safe default. A nested write op resets it.</summary>
    public void SourcesUnchanged() => _sourcesUnchanged = true;
    private bool _sourcesUnchanged;

    public void Dispose()
    {
        if (_handle is null) return;
        if (--_depth > 0) return; // nested hold on this thread - the outermost release publishes
        try { if (!_committed) IndexGeneration.Bump(_dir); }
        finally
        {
            t_held?.Remove(_key);
            _handle.Dispose();
            _handle = null;
        }
    }

    private static FileStream? TryOpen(string cacheDir)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            return new FileStream(Path.Combine(cacheDir, Name), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                  FileShare.None, bufferSize: 1, FileOptions.None);
        }
        catch (IOException) { return null; }               // another writer holds it
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Key(string cacheDir) => PathSafety.NormalizeDir(cacheDir);
}

/// <summary>
/// The cache's write generation: an opaque token rewritten (atomically) at the end of every committed write op
/// (see <see cref="IndexWriteLock"/>). Readers compare it to the token they loaded to detect another process's
/// writes. Missing file => "" (an index from before generations existed, or never written by a locked op).
/// The value is "&lt;write&gt; &lt;sources&gt;": the sources token moves only when a write may have changed sources (see
/// <see cref="IndexWriteLock.SourcesUnchanged"/>). Compared whole it still changes on every write, so a reader that
/// predates the sources token works unchanged; a value from such a writer has no sources token (<see cref="SourcesOf"/>
/// null), which readers treat as "sources may have changed".
/// </summary>
public static class IndexGeneration
{
    private const string Name = ".generation";

    /// <summary>The current token, "" if none was ever written, or null if it could not be read right now
    /// (racing a replace) - callers treat null as "can't tell, check again later", never as "changed".</summary>
    public static string? Read(string cacheDir)
    {
        var path = Path.Combine(cacheDir, Name);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
            catch (IOException) { Thread.Sleep(5); }
            catch (UnauthorizedAccessException) { Thread.Sleep(5); }
        }
        return null;
    }

    /// <summary>The sources token carried in a generation value, or null if it has none.</summary>
    public static string? SourcesOf(string? generation)
    {
        if (string.IsNullOrEmpty(generation)) return null;
        int sp = generation.IndexOf(' ');
        return sp > 0 && sp < generation.Length - 1 ? generation[(sp + 1)..] : null;
    }

    // carrySources: keep the current sources token (none yet -> a new one, the safe side).
    internal static string Bump(string cacheDir, bool carrySources = false)
    {
        var sources = (carrySources ? SourcesOf(Read(cacheDir)) : null) ?? Guid.NewGuid().ToString("N");
        var token = Guid.NewGuid().ToString("N") + " " + sources;
        AtomicFile.WriteText(Path.Combine(cacheDir, Name), w => w.Write(token));
        return token;
    }
}
