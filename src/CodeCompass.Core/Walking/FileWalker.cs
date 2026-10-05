using System.Collections.Concurrent;
using CodeCompass.Core.Config;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Storage;

namespace CodeCompass.Core.Walking;

/// <summary>A candidate file found by the walker. RelativePath is normalized with '/'. <see cref="MTimeTicks"/>
/// is the last-write time (UTC ticks) read from the directory enumeration - carried here so callers don't
/// re-stat the file for its timestamp (a second network round-trip over SMB).</summary>
public readonly record struct FileRecord(string RelativePath, string FullPath, long Size, long MTimeTicks);

/// <summary>
/// Iterative directory walk that prunes ignored directories before descending and skips ignored/oversized
/// files, so it never pays to enumerate junk. Symlinks and junctions (reparse points) are skipped to avoid
/// cycles. Size/mtime/attributes are read off the directory enumeration (one round-trip per directory,
/// no per-file re-stat). The walk can run several directory reads concurrently (see <c>walkThreads</c>):
/// over a latency-bound share, SMB2 credits let many enumerations be in flight at once, overlapping the
/// round-trips; locally it's a wash. Results stream lazily either way (bounded buffer), so a caller that
/// interleaves indexing still paces the walk.
/// </summary>
public sealed class FileWalker
{
    private readonly IgnoreRules _ignore;
    private readonly int _walkThreads; // 0 => resolve from config/env; 1 => serial; >1 => parallel

    public FileWalker(IgnoreRules ignore, int walkThreads = 0)
    {
        _ignore = ignore;
        _walkThreads = walkThreads;
    }

    /// <summary>Files skipped for exceeding the size cap during the last <see cref="Walk"/> (they are
    /// silently absent from the index otherwise). Valid after enumeration completes.</summary>
    public int OverCapSkipped => _overCapSkipped;
    public long LargestOverCapBytes { get; private set; }
    public string? LargestOverCapPath { get; private set; }

    /// <summary>Opt-in (survey/diagnostics): also collect the identities of over-cap files that are otherwise
    /// indexable (not an ignored binary/asset type), so a diagnostic can list "absent from the index due to
    /// SIZE" off the SAME walk the index uses instead of a second hand-rolled one that drifts. Default off -
    /// the build only needs the count/largest above, not the list, so it pays nothing.</summary>
    public bool CollectOverCapFiles { get; init; }
    public IReadOnlyList<(string Path, long Size)> OverCapFiles =>
        _overCapFiles is null ? Array.Empty<(string, long)>() : _overCapFiles;
    private List<(string Path, long Size)>? _overCapFiles;

    private int _overCapSkipped;
    private readonly object _largestLock = new(); // guards the largest-over-cap pair + over-cap list (parallel walk)

    /// <summary>Directory subtrees DROPPED during the last <see cref="Walk"/> because their listing failed
    /// (or came back empty) even after a network retry - i.e. their files are silently absent from the index.
    /// Valid after enumeration completes. D &gt; 0 means the walk was incomplete (a correctness gap), distinct
    /// from legitimately-empty directories, which are never counted.</summary>
    public int DroppedDirs => _droppedDirs;
    private int _droppedDirs;

    /// <summary>Directories skipped by a default name rule whose name often holds source (packages/, build/, ...) -
    /// see IgnoreRules.IsAmbiguousIgnoredDirectory. Valid after enumeration completes.</summary>
    public int AmbiguousDirsSkipped => _ambiguousDirsSkipped;
    private int _ambiguousDirsSkipped;

    private const int RetryDelayMs = 75; // brief pause before a single network re-read of a failed directory

    // Test-only fault-injection seam: invoked with each directory path just before it is enumerated, so a
    // test can simulate a transient SMB failure (throw once) and assert the walker retries into it.
    internal Action<string>? BeforeReadDirHook;

    // Test-only PROCESS-WIDE variant of the above, for driving a fault into a walk performed by code that
    // constructs its own FileWalker internally (e.g. RepositoryIndexer.Update). Null in normal operation.
    internal static Action<string>? GlobalBeforeReadDirHook;

    private static EnumerationOptions EnumOpts() => new()
    {
        // AttributesToSkip=0 preserves coverage (the default drops Hidden/System); IgnoreInaccessible skips
        // unreadable children instead of failing the whole directory; the walker does its own recursion.
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    public IEnumerable<FileRecord> Walk(string root)
    {
        root = Path.GetFullPath(root);
        _overCapSkipped = 0;
        _droppedDirs = 0;
        _ambiguousDirsSkipped = 0;
        LargestOverCapBytes = 0;
        LargestOverCapPath = null;
        _overCapFiles = CollectOverCapFiles ? new List<(string, long)>() : null;

        // A network root gets retry-on-failure directory reads: over SMB under concurrent load, a directory
        // listing can transiently throw (path-not-found on a dir that exists) or come back empty (a child
        // access error swallowed by IgnoreInaccessible), silently dropping a whole subtree from the index.
        bool network = NetworkPath.IsNetwork(root);
        int degree = _walkThreads > 0 ? _walkThreads : CodeCompassConfig.WalkThreads(Environment.ProcessorCount);
        return degree <= 1 ? WalkSerial(root, network) : WalkParallel(root, degree, network);
    }

    // Keep a subdirectory (not ignored, not a reparse point). Attributes are cached from the enumeration.
    private bool KeepSubdir(DirectoryInfo sub)
    {
        if (_ignore.IsIgnoredDirectory(sub.Name))
        {
            if (_ignore.IsAmbiguousIgnoredDirectory(sub.Name)) System.Threading.Interlocked.Increment(ref _ambiguousDirsSkipped);
            return false;
        }
        try { if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) return false; }
        catch { return false; }
        return true;
    }

    /// <summary>Is this file a symbolic link / junction? Reading one follows it, so a link in an untrusted clone
    /// (git creates them with core.symlinks) pointing at e.g. ~/.ssh/id_rsa would put that file's content in the
    /// index and quote it into search results. Only TRUE links count: other reparse points - OneDrive/cloud
    /// placeholders, dedup'd files - carry the same attribute but are ordinary content and must stay indexed.
    /// The link-target probe is paid only when the attribute is set (rare).</summary>
    internal static bool IsLink(FileSystemInfo info)
    {
        try
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return false;
            return info.LinkTarget is not null;
        }
        catch { return true; } // can't tell -> don't follow it
    }

    // Build a FileRecord for a file, or return false if it's skipped (over the size cap or ignored). Reads
    // size/mtime from the enumerated FileInfo (cached, no extra round-trip). Thread-safe over-cap accounting.
    private bool TryFileRecord(FileInfo file, string root, out FileRecord rec)
    {
        rec = default;
        long size;
        try { size = file.Length; } catch { return false; }

        if (size > _ignore.MaxFileSizeBytes) // count over-cap skips so the coverage gap isn't silent
        {
            System.Threading.Interlocked.Increment(ref _overCapSkipped);
            lock (_largestLock)
            {
                if (size > LargestOverCapBytes) { LargestOverCapBytes = size; LargestOverCapPath = file.FullName; }
                // Only files that would otherwise be indexed (not an ignored binary/asset extension) count as
                // "excluded by SIZE" - a big .png is excluded because it's an asset, not because of the cap.
                if (_overCapFiles is not null && !_ignore.IsIgnoredFile(file.Name, 0))
                    _overCapFiles.Add((Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), size));
            }
            return false;
        }

        if (_ignore.IsIgnoredFile(file.Name, size)) return false;
        if (IsLink(file)) return false;

        long mtime;
        try { mtime = file.LastWriteTimeUtc.Ticks; } catch { mtime = 0; }
        var rel = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
        rec = new FileRecord(rel, file.FullName, size, mtime);
        return true;
    }

    // Materialize one directory's entries. Over a network root, a transient SMB failure or a swallowed
    // empty listing is retried once before the subtree is given up - the difference between a complete index
    // and silently missing files under concurrent load. A genuinely-vanished directory (not-found AND no
    // longer on disk) is dropped silently; any OTHER give-up is counted and logged (never silent). null =>
    // no entries for this directory.
    private List<FileSystemInfo>? ReadDir(string dir, string root, EnumerationOptions opts, bool network)
    {
        var list = TryEnumerate(dir, opts, out var ex);

        if (ex is null)
        {
            // An empty listing on a share can be a transient child-access error swallowed by
            // IgnoreInaccessible rather than a truly-empty directory. Re-read once (no delay - empty dirs
            // enumerate instantly); if the retry sees entries, the first read had dropped children.
            if (network && list!.Count == 0)
            {
                var again = TryEnumerate(dir, opts, out var ex2);
                if (ex2 is null && again!.Count > 0) return again;
            }
            return list;
        }

        // A not-found for a directory that truly no longer exists is fine - it vanished between being
        // enqueued and read. Drop it silently; do not count it as a coverage gap.
        if (ex is DirectoryNotFoundException && !DirExists(dir)) return null;

        // It exists (or a non-not-found error): over a share, retry once after a brief pause before giving up.
        if (network)
        {
            System.Threading.Thread.Sleep(RetryDelayMs);
            var retry = TryEnumerate(dir, opts, out var ex3);
            if (ex3 is null) return retry; // transient - recovered the subtree
            ex = ex3;
        }

        // Give up on a directory we could not read though it exists: NOT silent - count it and warn, so a
        // coverage gap is visible instead of a confident-but-incomplete index.
        System.Threading.Interlocked.Increment(ref _droppedDirs);
        Log.For(root).Warn($"walk DROPPED a directory - its files are NOT indexed (search/def may return a false zero): {dir} ({ex.GetType().Name}: {ex.Message})");
        return null;
    }

    private List<FileSystemInfo>? TryEnumerate(string dir, EnumerationOptions opts, out Exception? error)
    {
        try
        {
            BeforeReadDirHook?.Invoke(dir); // test seam: may throw to simulate a transient failure
            GlobalBeforeReadDirHook?.Invoke(dir); // process-wide test seam (for walks made by internal walkers)
            error = null;
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList();
        }
        catch (Exception ex) { error = ex; return null; }
    }

    private static bool DirExists(string dir)
    {
        try { return Directory.Exists(dir); } catch { return false; }
    }

    private IEnumerable<FileRecord> WalkSerial(string root, bool network)
    {
        var opts = EnumOpts();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var entries = ReadDir(stack.Pop(), root, opts, network);
            if (entries is null) continue;
            foreach (var info in entries)
            {
                if (info is DirectoryInfo sub) { if (KeepSubdir(sub)) stack.Push(sub.FullName); }
                else if (info is FileInfo file) { if (TryFileRecord(file, root, out var rec)) yield return rec; }
            }
        }
    }

    // Bounded-parallel walk: worker threads pull directories off a shared queue and enumerate them
    // concurrently (overlapping SMB round-trips), pushing files to a bounded output the caller drains.
    // A pending-directory counter drives completion; a cancellation token lets an early-breaking consumer
    // stop the workers without a hang or a thread leak.
    private IEnumerable<FileRecord> WalkParallel(string root, int degree, bool network)
    {
        var opts = EnumOpts();
        var output = new BlockingCollection<FileRecord>(8192); // bounded => backpressure paces the walk
        var dirs = new BlockingCollection<string>();
        var cts = new System.Threading.CancellationTokenSource();
        var token = cts.Token;
        int pending = 1; // root
        dirs.Add(root);

        var workers = new System.Threading.Tasks.Task[degree];
        for (int w = 0; w < degree; w++)
        {
            workers[w] = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    foreach (var dir in dirs.GetConsumingEnumerable(token))
                    {
                        try
                        {
                            var entries = ReadDir(dir, root, opts, network);
                            if (entries is not null)
                                foreach (var info in entries)
                                {
                                    if (info is DirectoryInfo sub)
                                    {
                                        // Increment BEFORE enqueue so the pending count can't hit zero
                                        // while a child is in flight. (The current dir still holds its +1.)
                                        if (KeepSubdir(sub)) { System.Threading.Interlocked.Increment(ref pending); dirs.Add(sub.FullName); }
                                    }
                                    else if (info is FileInfo file)
                                    {
                                        if (TryFileRecord(file, root, out var rec)) output.Add(rec, token);
                                    }
                                }
                        }
                        finally
                        {
                            // When the last outstanding directory is done, no more will ever be added.
                            if (System.Threading.Interlocked.Decrement(ref pending) == 0) dirs.CompleteAdding();
                        }
                    }
                }
                catch (OperationCanceledException) { /* consumer broke early: stop */ }
                catch (InvalidOperationException) { /* CompleteAdding raced GetConsumingEnumerable: stop */ }
            }, token);
        }

        // Once every worker has exited, nothing more will be produced. A worker that died on an UNEXPECTED
        // exception (its own catch handles cancellation + the CompleteAdding race) may have left directories
        // unwalked - surface that as an incomplete walk (DroppedDirs) so callers suppress deletion
        // reconciliation, and log it, rather than silently returning a partial file list that looks like a
        // smaller repo.
        System.Threading.Tasks.Task.WhenAll(workers).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                foreach (var inner in t.Exception.Flatten().InnerExceptions)
                {
                    if (inner is OperationCanceledException) continue;
                    System.Threading.Interlocked.Increment(ref _droppedDirs);
                    try { Log.Global.Warn($"walk worker faulted ({inner.GetType().Name}): {inner.Message}; walk marked incomplete"); } catch { }
                }
            }
            try { output.CompleteAdding(); } catch { }
        });

        try
        {
            foreach (var rec in output.GetConsumingEnumerable())
                yield return rec;
        }
        finally
        {
            // Normal end or early break: cancel the workers and let them unwind before returning, so no
            // thread is left blocked on output.Add and nothing is disposed out from under a running worker.
            cts.Cancel();
            try { System.Threading.Tasks.Task.WhenAll(workers).Wait(2000); } catch { }
            cts.Dispose();
        }
    }
}
