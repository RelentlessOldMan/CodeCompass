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

    private int _overCapSkipped;
    private readonly object _largestLock = new(); // guards the largest-over-cap pair (parallel walk)

    /// <summary>Directory subtrees DROPPED during the last <see cref="Walk"/> because their listing failed
    /// (or came back empty) even after a network retry - i.e. their files are silently absent from the index.
    /// Valid after enumeration completes. D &gt; 0 means the walk was incomplete (a correctness gap), distinct
    /// from legitimately-empty directories, which are never counted.</summary>
    public int DroppedDirs => _droppedDirs;
    private int _droppedDirs;

    private const int RetryDelayMs = 75; // brief pause before a single network re-read of a failed directory

    // Test-only fault-injection seam: invoked with each directory path just before it is enumerated, so a
    // test can simulate a transient SMB failure (throw once) and assert the walker retries into it.
    internal Action<string>? BeforeReadDirHook;

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
        LargestOverCapBytes = 0;
        LargestOverCapPath = null;

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
        if (_ignore.IsIgnoredDirectory(sub.Name)) return false;
        try { if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) return false; }
        catch { return false; }
        return true;
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
            }
            return false;
        }

        if (_ignore.IsIgnoredFile(file.Name, size)) return false;

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

        // Once every worker has exited, nothing more will be produced.
        System.Threading.Tasks.Task.WhenAll(workers).ContinueWith(_ => { try { output.CompleteAdding(); } catch { } });

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
