using System.Collections.Concurrent;
using System.Diagnostics;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Config;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using CodeCompass.Core.Text;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Indexing;

// TrigramPostings is the sum of each document's distinct-trigram count - a deterministic total
// (independent of how files are split across segments), unlike a per-segment distinct-term sum which
// double-counts shared trigrams and varies run to run with parallel scheduling.
public sealed record IndexStats(int Files, long Bytes, long TrigramPostings, int Symbols, double Seconds, long IndexBytes, int Cores);

public sealed record UpdateStats(int Added, int Modified, int Removed, double Seconds, bool FullRebuild);

public sealed record ChangeCounts(int Added, int Modified, int Removed);

/// <summary>
/// Builds and loads a repository's on-disk index: a memory-mapped segmented trigram index,
/// a memory-mapped segmented symbol index, and a content snapshot for change detection.
/// Both indexes stream to disk during build (bounded RAM) and are read via mmap (bounded
/// RAM at query time). Callers own returned index instances and should Dispose long-lived ones.
/// </summary>
public static class RepositoryIndexer
{
    private const int RebuildThreshold = 2000;

    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, IndexStats Stats) Build(
        string root, Action<int, long>? onProgress = null, System.Threading.CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        // Every mutation of a cache holds the cross-process write lock (see IndexWriteLock): another session or a
        // terminal command writing the same cache waits for us instead of colliding on segment numbers/manifests.
        using var writeLock = IndexWriteLock.Acquire(IndexStore.GetCacheDir(root), ct);
        return BuildLocked(root, onProgress, ct);
    }

    private static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, IndexStats Stats) BuildLocked(
        string root, Action<int, long>? onProgress, System.Threading.CancellationToken ct)
    {
        CodeCompassConfig.Load(root);
        var dir = IndexStore.GetCacheDir(root);
        var ignore = new IgnoreRules();
        var walker = new FileWalker(ignore);
        int cores = DegreeOfParallelism();
        var (textBudget, symBudget) = SegmentBudgets(cores);

        int progressFiles = 0;
        long progressBytes = 0;
        System.Threading.Timer? progressTimer = onProgress is null ? null
            : new System.Threading.Timer(_ => onProgress(Volatile.Read(ref progressFiles), Interlocked.Read(ref progressBytes)),
                                         null, 500, 500);

        // What each worker is chewing on right now (thread id -> file + when it started), so the
        // watchdog can name the exact file(s) wedging a build instead of guessing.
        var inFlight = new ConcurrentDictionary<int, (string Path, long Size, long StartMs)>();
        var clock = Stopwatch.StartNew();

        // Stall watchdog. Two triggers, both content-independent: (1) the byte counter is frozen (a
        // hard stall), or (2) any worker has held a single file longer than the stall window - which
        // catches the "single-threaded tail" (one slow file grinding one worker while the counter
        // still creeps from the others, so a zero-progress test alone stays silent). It logs the
        // files in flight and how long each has been held, naming the culprit rather than guessing.
        //
        // Runs on a DEDICATED thread, not a Timer: Parallel.ForEach below saturates the thread pool,
        // and Timer callbacks are queued to that same pool - so a pool-based watchdog is starved for
        // the whole build and never fires (this is why the earlier Timer watchdog stayed silent under
        // load). A dedicated thread checks on schedule regardless of pool pressure.
        int stallSec = StallWarnSeconds();
        long stallMs = StallWarnMsForTests ?? stallSec * 1000L;
        var buildDone = new ManualResetEventSlim(false);
        var watchdog = new Thread(() =>
        {
            long prevBytes = -1;
            int dumps = 0;
            while (!buildDone.Wait((int)stallMs))
            {
                long curBytes = Interlocked.Read(ref progressBytes);
                int curFiles = Volatile.Read(ref progressFiles);
                long now = clock.ElapsedMilliseconds;
                bool hardStall = curBytes == prevBytes && curBytes > 0;
                prevBytes = curBytes;

                var held = inFlight.ToArray();
                var stuck = held.Where(k => now - k.Value.StartMs >= stallMs).ToArray();
                if (!(hardStall || stuck.Length > 0) || dumps >= 3) continue;
                dumps++;

                var log = Log.For(root);
                var (headline, advice) = StallReport(stuck.Length, NetworkPath.IsNetwork(root), hardStall, stallSec, curFiles, curBytes);
                log.Warn(headline);
                if (held.Length == 0)
                    log.Warn("  (no files in flight - the stall is in a post-parse/finalize step, not a single file)");
                foreach (var kv in held.OrderByDescending(k => now - k.Value.StartMs))
                    log.Warn($"  worker {kv.Key}: {kv.Value.Path} ({kv.Value.Size / 1048576.0:F1} MB) held {(now - kv.Value.StartMs) / 1000.0:F0}s");
                log.Warn(advice);

                // Also surface on-screen (stderr) once, so a stall isn't a silent frozen progress bar.
                // stderr is safe for the MCP server too (stdout is its protocol channel, stderr is logs).
                if (dumps == 1)
                {
                    var top = held.OrderByDescending(k => now - k.Value.StartMs).FirstOrDefault();
                    Console.Error.WriteLine($"\n[codecompass] {headline}");
                    if (top.Value.Path is not null)
                        Console.Error.WriteLine($"[codecompass]   stuck on: {top.Value.Path} ({top.Value.Size / 1048576.0:F1} MB). See `codecompass logs` for the full list.");
                }
            }
        }) { IsBackground = true, Name = "cc-index-watchdog" };
        watchdog.Start();

        // Bound how much file content is read at once so a large file cap can't let every core
        // load a multi-GB file simultaneously and exhaust RAM. Scales with machine memory.
        var reads = new ByteBudget(CodeCompassConfig.ReadBudgetBytes());

        var snapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var textSegFiles = new ConcurrentBag<(int Num, string Name)>();
        var symSegFiles = new ConcurrentBag<(int Num, string Name)>();
        var posSidecars = new ConcurrentBag<string>(); // positional sidecars written for large files
        int textSegCounter = SegmentedIndex.NextSegmentNumber(dir);
        int symSegCounter = SegmentedSymbolIndex.NextSegmentNumber(dir);
        long totalBytes = 0;
        long totalPostings = 0;
        int totalSymbolSkipped = 0;
        int totalMemorySkipped = 0;
        var gate = new object();

        var sw = Stopwatch.StartNew();

        // ONE enumeration up front: materialize the file list. The walk stats every file's size regardless,
        // so this is the SAME pass, not a second one (no extra SMB round-trips) - it just lets us measure the
        // repo's SHAPE before indexing and adapt the sidecar cutoff to it, in a single deterministic build
        // (no "measure now, apply on the next index" divergence). Memory is O(files) of small records - the
        // same order as the snapshot we already hold.
        var files = walker.Walk(root).ToList();
        bool isNetwork = Storage.NetworkPath.IsNetwork(root);
        var landscape = RepoLandscape.Compute(files.ConvertAll(f => f.Size));
        long sidecarThreshold = landscape.EffectiveSidecarThreshold(isNetwork);
        if (sidecarThreshold != RepoLandscape.DefaultSidecarThreshold)
            Log.For(root).Info($"adaptive sidecar threshold -> {sidecarThreshold / 1048576} MB " +
                               $"(network={isNetwork}; {landscape.Summary()})");

        // Each worker fills private trigram + symbol segment buffers lock-free and flushes them
        // to disk at their byte budgets, so build RAM is bounded regardless of repo size.
        //
        // NoBuffering (one item at a time) instead of the default growing-chunk partitioner: general
        // load-balancing hygiene for variable-cost files. When expensive files CLUSTER in directory
        // order (e.g. a vendored minified-JS or generated dir), chunking hands one worker an all-
        // expensive chunk to grind while others idle; one-at-a-time hand-out keeps every core fed. The
        // per-item sync cost is negligible next to read+parse. NOTE: this is NOT a fix for a build that
        // crawls because many individually-slow files are being parsed in parallel (e.g. big numeric
        // data-blob sources at ~1s/MB) - that is bounded by the symbol-size cap, not by scheduling
        // (confirmed on a real 90GB repo: NoBuffering made no difference to that case).
        try
        {
        Parallel.ForEach(
            Partitioner.Create(files, EnumerablePartitionerOptions.NoBuffering),
            // CancellationToken: a re-point/shutdown cancels the server's token so a long rebuild bails
            // promptly - freeing the file-watcher's Dispose barrier and not racing teardown - instead of
            // running to completion. ParallelOptions stops scheduling further files and throws
            // OperationCanceledException once the token trips; the finally below still tears the build down.
            new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = ct },
            () => new BuildWorker(),
            (file, _, worker) =>
            {
                int tid = Environment.CurrentManagedThreadId;
                var mtime = file.MTimeTicks; // from the walk enumeration (no extra stat)
                long readStart = DateTime.UtcNow.Ticks; // before the bytes are read (LedgerTrust)

                // Large files are streamed, not read whole: the whole-file path decodes into a single
                // .NET string, which caps near ~1 GB of text regardless of RAM. Streaming hashes and
                // trigram-indexes in chunks (bounded memory), so files up to the file cap index without
                // that ceiling. Symbols are not extracted for streamed files (tree-sitter needs the whole
                // string, and such files are over the symbol cap anyway); they stay text-searchable.
                // A streamed file reserves a small fixed footprint, since it never holds the whole file.
                if (file.Size >= LargeFileIndexer.StreamThresholdBytes)
                {
                    reads.Acquire(StreamingReserveBytes);
                    inFlight[tid] = (file.RelativePath, file.Size, clock.ElapsedMilliseconds);
                    try
                    {
                        // Streamed through its own stream, so bracket it with attribute-only stats (LedgerTrust).
                        var bigBefore = Storage.FileIdentity.OfPath(file.FullPath);
                        bool streamed = LargeFileIndexer.TryStreamIndex(file.FullPath, out var big, out var len, out var bigHash, out var bin, out var bigBlocks);
                        var bigAfter = Storage.FileIdentity.OfPath(file.FullPath);
                        FileState BigRecord(long size, string h) => LedgerTrust.Record(
                            new ReadStamp(file.Size, mtime, bigBefore, bigAfter, readStart, DateTime.UtcNow.Ticks), isNetwork, null, 0, size, h);
                        if (streamed)
                        {
                            worker.Text.AddDocument(file.RelativePath, big);
                            worker.TrigramPostings += big.Length;
                            if (bigBlocks is not null)
                            {
                                PositionalSidecar.Write(dir, file.RelativePath, bigBlocks); // block index for cheap large-file search
                                posSidecars.Add(PositionalSidecar.SidecarName(file.RelativePath));
                            }
                            worker.Snapshot[file.RelativePath] = BigRecord(len, bigHash);
                            worker.Bytes += len;
                            // Streamed files are text-searchable but get no symbols (tree-sitter needs the
                            // whole string). If it's a symbol-bearing language, count it so a go-to-definition
                            // zero can disclose that a definition might live here.
                            if (LanguageRegistry.ForPath(file.RelativePath) is not null) worker.SymbolSkipped++;
                            Interlocked.Increment(ref progressFiles);
                            Interlocked.Add(ref progressBytes, len);
                            if (worker.Text.ApproxBytes >= textBudget) FlushText(worker, dir, ref textSegCounter, textSegFiles);
                        }
                        else if (bin) worker.Snapshot[file.RelativePath] = BigRecord(file.Size, FileState.BinaryHash);
                        else
                            // WARN, not Debug: a large file that fails to read (vs a deliberate binary skip)
                            // is silently absent from the index otherwise - the exact "N fewer files, no log"
                            // symptom seen over SMB. Visible at the default log level so it's diagnosable.
                            Log.For(root).Warn($"NOT INDEXED - large file unreadable (transient I/O over a share?): {file.RelativePath}");
                        return worker;
                    }
                    catch (OutOfMemoryException)
                    {
                        // Same memory-pressure backstop as the whole-file path: reclaim, skip this file,
                        // keep building. It's left out of the snapshot so `update` re-indexes it later.
                        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
                        worker.MemorySkipped++;
                        Log.For(root).Warn($"NOT INDEXED - skipped under memory pressure (machine near its commit limit): " +
                            $"{file.RelativePath}. Close other memory-heavy apps, or lower CODECOMPASS_THREADS / " +
                            $"CODECOMPASS_READ_BUDGET_MB, then re-run 'codecompass update' to pick it up.");
                        return worker;
                    }
                    finally { inFlight.TryRemove(tid, out var _sg); reads.Release(StreamingReserveBytes); }
                }

                // Whole-file path (normal files). Reserve the real processing footprint, not just the
                // raw size: while trigrams are computed the raw bytes (1x) and the decoded UTF-16 string
                // (2x) are both live, so peak is ~3x the file size. Reserving 3x makes the budget's
                // RAM-scaling honest - a budget of N genuinely fits a file up to ~N/3, and a bigger one
                // reserves the whole budget and runs solo (ByteBudget clamps the reservation, no deadlock).
                long footprint = ProcessingFootprint(file.Size);
                reads.Acquire(footprint);
                inFlight[tid] = (file.RelativePath, file.Size, clock.ElapsedMilliseconds); // for the watchdog
                try
                {
                    OnFileStartForTests?.Invoke(file.RelativePath);
                    byte[] bytes;
                    Storage.FileIdentity before = default, after = default;
                    // OutOfMemory here is NOT an I/O error: it means the machine can't commit the read.
                    // Let it fall to the memory-pressure handler below (which reclaims and skips the file
                    // without aborting the whole build) rather than mislabeling it "transient I/O".
                    // The read handle is statted just before and after the read (LedgerTrust, ledger v2).
                    try { bytes = Storage.FileIdentity.ReadAllBytes(file.FullPath, out before, out after); }
                    catch (OutOfMemoryException) { throw; }
                    // WARN, not Debug: a read failure silently drops the file from the index (the "N fewer
                    // files, no log" symptom over SMB); surface it at the default level so it's diagnosable.
                    catch (Exception ex) { Log.For(root).Warn($"NOT INDEXED - file unreadable (transient I/O over a share?): {file.RelativePath}: {ex.Message}"); return worker; }
                    FileState Record(string h) => LedgerTrust.Record(
                        new ReadStamp(file.Size, mtime, before, after, readStart, DateTime.UtcNow.Ticks), isNetwork, null, 0, bytes.Length, h);
                    if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000))))
                    {
                        worker.Snapshot[file.RelativePath] = Record(FileState.BinaryHash);
                        return worker;
                    }

                    var content = TextDecoder.FromBytes(bytes);
                    var hash = ContentHasher.Hash(bytes);

                    var tg = TrigramIndex.ComputeTrigrams(content, worker.TrigramScratch);
                    worker.Text.AddDocument(file.RelativePath, tg);
                    worker.TrigramPostings += tg.Length;

                    // Mid-size files (>= sidecar threshold, but under the streaming threshold so still whole-read
                    // here) get a positional block sidecar built from the bytes already in memory - so a search
                    // whose trigrams land in a 10-128 MB file reads only the candidate blocks, not the whole file.
                    if (bytes.Length >= sidecarThreshold)
                    {
                        var midBlocks = LargeFileIndexer.BuildBlocks(bytes);
                        if (midBlocks is not null)
                        {
                            PositionalSidecar.Write(dir, file.RelativePath, midBlocks);
                            posSidecars.Add(PositionalSidecar.SidecarName(file.RelativePath));
                        }
                    }
                    if (LanguageRegistry.ForPath(file.RelativePath) is not null)
                    {
                        // A symbol-bearing file whose symbols are skipped by a cap / data-blob guard is
                        // counted (not parsed) so a go-to-definition zero can be honest about it.
                        if (worker.Extractor.WouldSkipSymbols(file.RelativePath, content)) worker.SymbolSkipped++;
                        else foreach (var s in worker.Extractor.Extract(file.RelativePath, content)) worker.Symbols.Add(s);
                    }
                    worker.Snapshot[file.RelativePath] = Record(hash);
                    worker.Bytes += bytes.Length;
                    Interlocked.Increment(ref progressFiles);
                    Interlocked.Add(ref progressBytes, bytes.Length);

                    if (worker.Text.ApproxBytes >= textBudget) FlushText(worker, dir, ref textSegCounter, textSegFiles);
                    if (worker.Symbols.ApproxBytes >= symBudget) FlushSymbols(worker, dir, ref symSegCounter, symSegFiles);
                    return worker;
                }
                catch (OutOfMemoryException)
                {
                    // Memory-pressure backstop: the machine ran out of committable memory mid-file (a big
                    // file read/decode when other processes hold most of the commit charge). The failed
                    // allocation didn't happen, so the process is intact - reclaim it, skip THIS file, and
                    // keep building instead of aborting the whole index. The file is left out of the
                    // snapshot, so a later `codecompass update` re-indexes it once memory frees (self-heals).
                    // The read budget is normally sized to prevent this (it caps against available commit);
                    // this catches the case where the machine tightened further mid-build.
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
                    worker.MemorySkipped++;
                    Log.For(root).Warn($"NOT INDEXED - skipped under memory pressure (machine near its commit limit): " +
                        $"{file.RelativePath}. Close other memory-heavy apps, or lower CODECOMPASS_THREADS / " +
                        $"CODECOMPASS_READ_BUDGET_MB, then re-run 'codecompass update' to pick it up.");
                    return worker;
                }
                finally { inFlight.TryRemove(tid, out var _gone); reads.Release(footprint); }
            },
            worker =>
            {
                FlushText(worker, dir, ref textSegCounter, textSegFiles);
                FlushSymbols(worker, dir, ref symSegCounter, symSegFiles);
                lock (gate)
                {
                    foreach (var (rel, state) in worker.Snapshot) snapshot[rel] = state;
                    totalBytes += worker.Bytes;
                    totalPostings += worker.TrigramPostings;
                    totalSymbolSkipped += worker.SymbolSkipped;
                    totalMemorySkipped += worker.MemorySkipped;
                }
                worker.Extractor.Dispose();
            });
            ct.ThrowIfCancellationRequested(); // cancelled during, or right after, the loop (before the persist below)
        }
        catch (OperationCanceledException)
        {
            // Aborted mid-build by a re-point/shutdown. Delete the segment/sidecar files THIS cancelled build
            // flushed: they are its own NEW (higher-numbered) segments, never referenced by any manifest (we
            // never reached persist), so removing them is safe - and it prevents both cache litter and the
            // manifest-less recovery path (SegmentedIndex.Load) later mistaking them for live segments, which
            // would resurrect stale/duplicate docs. The live index's own manifest + segments are untouched.
            foreach (var (_, n) in textSegFiles) TryDeleteCacheFile(dir, n);
            foreach (var (_, n) in symSegFiles) TryDeleteCacheFile(dir, n);
            foreach (var n in posSidecars) TryDeleteCacheFile(dir, n);
            throw;
        }
        finally
        {
            // Always tear down the watchdog thread + progress timer, even if the parallel build threw
            // (including OperationCanceledException on a re-point/shutdown) - otherwise a cancelled build
            // would leak the dedicated watchdog thread and the progress Timer.
            sw.Stop();
            progressTimer?.Dispose();
            buildDone.Set();           // stop the watchdog thread
            watchdog.Join(1000);
            buildDone.Dispose();
        }
        onProgress?.Invoke(progressFiles, progressBytes);

        if (walker.OverCapSkipped > 0)
        {
            var largest = walker.LargestOverCapPath is null ? "" :
                $" (largest: {Path.GetFileName(walker.LargestOverCapPath)} {walker.LargestOverCapBytes / 1048576.0:F0} MB)";
            Log.For(root).Info($"skipped {walker.OverCapSkipped:N0} file(s) over the " +
                $"{ignore.MaxFileSizeBytes / 1048576.0:F0} MB size cap{largest} - not in the index. " +
                $"Raise CODECOMPASS_MAX_FILE_MB to include them.");
        }

        if (totalMemorySkipped > 0)
        {
            // A coverage gap, but a TRANSIENT one (unlike the size cap): the machine was out of committable
            // memory during this build. NOT persisted to meta - the files are simply absent from the
            // snapshot, so `codecompass update` re-indexes them once memory frees. Surface loudly on both
            // the log and stderr so a partial index isn't mistaken for a complete one.
            var msg = $"{totalMemorySkipped:N0} file(s) were SKIPPED under memory pressure and are NOT in this index " +
                      "(the machine was near its commit limit). Close other memory-heavy apps, or lower " +
                      "CODECOMPASS_THREADS / CODECOMPASS_READ_BUDGET_MB, then re-run 'codecompass update' to pick them up.";
            Log.For(root).Warn(msg);
            Console.Error.WriteLine($"[codecompass] {msg}");
        }

        if (walker.DroppedDirs > 0)
            Log.For(root).Warn($"walk DROPPED {walker.DroppedDirs:N0} director(y/ies) that could not be read even after retry - " +
                "their files are NOT in this index (search/find_definition may return a false zero). This is usually a " +
                "transient network error under load; re-run 'codecompass update' (or index) to pick them up. See the WARN lines above for paths.");

        var textOrdered = textSegFiles.OrderBy(x => x.Num).Select(x => x.Name).ToList();
        var symOrdered = symSegFiles.OrderBy(x => x.Num).Select(x => x.Name).ToList();
        var text = SegmentedIndex.FromSegmentFiles(root, dir, textOrdered, textSegCounter, textBudget);
        var symbols = SegmentedSymbolIndex.FromSegmentFiles(dir, symOrdered, symSegCounter, symBudget);
        symbols.QueryIgnore = IgnoreRules.ForRoot(root);
        PositionalSidecar.CleanupOrphans(dir, new HashSet<string>(posSidecars, StringComparer.OrdinalIgnoreCase)); // drop sidecars for files no longer large/present
        SaveSnapshot(root, snapshot);

        var stats = new IndexStats(text.DocumentCount, totalBytes, totalPostings,
                                   symbols.Count, sw.Elapsed.TotalSeconds, text.IndexBytes, cores);
        Log.For(root).Debug($"build stats: {stats.Files:N0} files, {stats.TrigramPostings:N0} trigram postings, " +
                            $"{stats.Symbols:N0} symbols, index {stats.IndexBytes / 1048576.0:F0} MB, " +
                            $"{cores} core(s), {stats.Seconds:F1}s");
        // Record path/version/time + coverage (files excluded by the size cap, and files indexed for text
        // but with no symbols extracted) so the search tools can be honest about a zero result and
        // doctor/cache can report by real path.
        IndexMetaFile.Write(root, text.DocumentCount, walker.OverCapSkipped, totalSymbolSkipped, walker.DroppedDirs,
                            sidecarThreshold, landscape.Summary(), walker.AmbiguousDirsSkipped);
        return (text, symbols, stats);
    }

    private static void FlushText(BuildWorker w, string dir, ref int counter, ConcurrentBag<(int, string)> files)
    {
        if (w.Text.DocCount == 0) return;
        int num = Interlocked.Increment(ref counter) - 1;
        var name = SegmentedIndex.SegmentFileName(num);
        w.Text.WriteTo(Path.Combine(dir, name));
        files.Add((num, name));
        w.Text = new SegmentBuilder();
    }

    private static void FlushSymbols(BuildWorker w, string dir, ref int counter, ConcurrentBag<(int, string)> files)
    {
        if (w.Symbols.Count == 0) return;
        int num = Interlocked.Increment(ref counter) - 1;
        var name = SegmentedSymbolIndex.SegmentFileName(num);
        w.Symbols.WriteTo(Path.Combine(dir, name));
        files.Add((num, name));
        w.Symbols = new SymbolSegmentBuilder();
    }

    // Best-effort removal of a cache file (a segment/sidecar an aborted build flushed). Never throws - a file
    // that's already gone or momentarily locked is fine to leave; the next full build's CleanupOrphans gets it.
    private static void TryDeleteCacheFile(string dir, string name)
    { try { File.Delete(Path.Combine(dir, name)); } catch { } }

    /// <summary>
    /// True when the incremental path should compact by doing a full rebuild. Every incremental
    /// batch appends a segment and can add tombstones that are never otherwise reclaimed, so a
    /// long-running watch session accumulates unbounded tiny segments + tombstones (slower search,
    /// rising RAM). Rebuilding resets to a clean, tombstone-free set of segments. Threshold via
    /// CODECOMPASS_COMPACT_SEGMENTS (default 64); under normal editing this fires rarely.
    /// </summary>
    public static bool NeedsCompaction(SegmentedIndex text, SegmentedSymbolIndex symbols)
    {
        int threshold = CompactSegmentThreshold();
        return text.SegmentCount >= threshold || symbols.SegmentCount >= threshold;
    }

    private static int CompactSegmentThreshold() => CodeCompassConfig.CompactSegments();

    private static int StallWarnSeconds() => CodeCompassConfig.StallWarnSec();

    // Test seams (null in production). OnFileStartForTests runs on the worker as it starts a whole-read file,
    // after the file is registered with the watchdog - a test holds a file there (to trip the watchdog) or
    // throws OutOfMemoryException (to hit the per-file memory backstop). StallWarnMsForTests shortens the
    // watchdog window below the 5 s config floor so those tests run in well under a second.
    internal static Action<string>? OnFileStartForTests;
    internal static long? StallWarnMsForTests;

    /// <summary>The stall watchdog's headline and advice. Over a share, a worker "held" on a big file is usually
    /// slow TRANSFER, not slow parse - and if the byte counter is still climbing (no hard stall) it finishes on its
    /// own, so "exclude it / lower the caps" (the local advice) is the wrong fix there and is worded differently.</summary>
    internal static (string Headline, string Advice) StallReport(int stuck, bool onNetwork, bool hardStall,
                                                                  int stallSec, int files, long bytes)
    {
        string headline = stuck > 0
            ? (onNetwork
                ? $"indexing slow: {stuck} worker(s) held one large file >{stallSec}s at {files:N0} files ({bytes / 1048576.0:F0} MB) over a network share - likely slow transfer, not a stuck build."
                : $"indexing slow: {stuck} worker(s) held one file >{stallSec}s at {files:N0} files ({bytes / 1048576.0:F0} MB) - a slow-to-parse file is grinding a worker while others may idle.")
            : $"indexing made no progress for ~{stallSec}s at {files:N0} files ({bytes / 1048576.0:F0} MB).";
        string advice = onNetwork && !hardStall
            ? "Over a network share this is normal for large files while overall progress continues - it should finish. Exclude it (CODECOMPASS_IGNORE) only if the build never completes."
            : "If a file has been held many seconds it is the culprit: exclude it (CODECOMPASS_IGNORE) or lower CODECOMPASS_MAX_SYMBOL_MB / CODECOMPASS_MAX_FILE_MB.";
        return (headline, advice);
    }

    // Read-budget reservation for a streamed (large) file. Streaming never holds the whole file - only
    // ~1 MB byte chunks, a char buffer, and the distinct-trigram set - so a fixed, modest reservation
    // (independent of file size) is right; it lets several large files stream concurrently within the
    // budget instead of each reserving 3x its multi-GB size.
    private const long StreamingReserveBytes = 256L * 1024 * 1024;

    // Approximate peak RAM to hold one file in flight while it's indexed: raw bytes (1x) + the decoded
    // UTF-16 string (2x) live simultaneously during trigram computation (~3x). Used to reserve against
    // the read budget so its RAM-scaling is honest for large files. Guards against long overflow.
    private const long ProcessingFootprintMultiple = 3;
    private static long ProcessingFootprint(long fileSize) =>
        fileSize > long.MaxValue / ProcessingFootprintMultiple ? long.MaxValue : fileSize * ProcessingFootprintMultiple;

    /// <summary>Indexing parallelism: CODECOMPASS_THREADS / config `threads` if set, else all cores.</summary>
    public static int DegreeOfParallelism() => CodeCompassConfig.Threads(Environment.ProcessorCount);

    /// <summary>
    /// The shared "first encounter" size policy: is <paramref name="root"/> over the auto-index budget
    /// (<c>maxAutoMb</c>, default 100 MB)? A small root is indexed automatically/inline; a large one is
    /// deferred to an explicit <c>codecompass index</c>. Used identically for the project root (MCP
    /// startup) and for linked roots (<c>link add</c>), so both behave the same. Stops summing the moment
    /// the limit is crossed. Caller should <see cref="CodeCompassConfig.Load"/> the relevant config first.
    /// </summary>
    public static bool ExceedsAutoLimit(string root, out long totalBytes) =>
        ExceedsAutoLimit(root, CodeCompassConfig.Current, out totalBytes);

    /// <summary>As <see cref="ExceedsAutoLimit(string,out long)"/> but against an EXPLICIT config, so a
    /// caller can gate a root other than the ambient one (e.g. a linked root) without loading and
    /// clobbering the global config from a background thread.</summary>
    public static bool ExceedsAutoLimit(string root, RepoConfig cfg, out long totalBytes)
    {
        long limit = CodeCompassConfig.MaxAutoBytes(cfg);
        long sum = 0;
        foreach (var f in new FileWalker(new IgnoreRules()).Walk(Path.GetFullPath(root)))
        {
            sum += f.Size;
            if (sum > limit) { totalBytes = sum; return true; }
        }
        totalBytes = sum;
        return false;
    }

    /// <summary>
    /// Per-worker trigram/symbol segment byte budgets, scaled so total build buffers
    /// (cores x (text+symbol)) fit a fraction of available RAM - keeps first-time builds
    /// within reach on small machines. Override the text budget with CODECOMPASS_SEGMENT_MB.
    /// </summary>
    public static (long Text, long Symbol) SegmentBudgets(int cores)
    {
        cores = Math.Max(1, cores);
        if (CodeCompassConfig.SegmentMbOverride() is int mb)
        {
            long t = mb * 1024L * 1024;
            return (t, Math.Max(2L * 1024 * 1024, t / 2));
        }

        long avail;
        try { avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { avail = 8L * 1024 * 1024 * 1024; }
        if (avail <= 0) avail = 8L * 1024 * 1024 * 1024;

        long totalBuffers = Math.Clamp(avail / 8, 128L * 1024 * 1024, 4L * 1024 * 1024 * 1024);
        long perCore = totalBuffers / cores;
        long text = Math.Clamp(perCore * 2 / 3, 4L * 1024 * 1024, 128L * 1024 * 1024);
        long symbol = Math.Clamp(perCore / 3, 2L * 1024 * 1024, 64L * 1024 * 1024);
        return (text, symbol);
    }

    private sealed class BuildWorker
    {
        public SegmentBuilder Text = new();
        public SymbolSegmentBuilder Symbols = new();
        public Dictionary<string, FileState> Snapshot { get; } = new(StringComparer.Ordinal);
        public TreeSitterSymbolExtractor Extractor { get; } = new();
        public readonly HashSet<long> TrigramScratch = new(); // reused per file (this worker's thread only) to avoid a per-file HashSet alloc
        public long Bytes;
        public long TrigramPostings; // sum of per-doc distinct-trigram counts (deterministic total)
        public int SymbolSkipped;    // symbol-language files indexed for text but with NO symbols extracted
        public int MemorySkipped;    // files skipped because the machine was out of committable memory (transient; not persisted)
    }

    /// <param name="onScan">Optional heartbeat: invoked with the running count of files stat-walked,
    /// so a caller can show progress during the (silent, potentially slow over a network share)
    /// change-detection pass.</param>
    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, UpdateStats Stats) Update(
        string root, Action<int>? onScan = null, System.Threading.CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        using var writeLock = IndexWriteLock.Acquire(IndexStore.GetCacheDir(root), ct);
        CodeCompassConfig.Load(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols, out var why)) { ThrowIfMustNotRebuild(root, why); return FullRebuild(root, text, symbols, sw, ct); }
        if (!TryLoadSnapshot(root, out var old)) return FullRebuild(root, text, symbols, sw, ct);

        using (old)
        {
            var dir = IndexStore.GetCacheDir(root);
            var walker = new FileWalker(new IgnoreRules());
            var newSnapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int added = 0, modified = 0, removed = 0, walked = 0;
            // The sidecar cutoff is a repo property decided at BUILD time (adaptive to shape + network) and
            // recorded in meta. An incremental update MUST reuse the same value and re-persist it - otherwise
            // an edited mid-size file gets written/deleted under a different cutoff, leaving a stale or
            // orphaned sidecar, and the next update would keep drifting.
            var priorMeta = IndexMetaFile.Read(root);
            long sidecarThreshold = priorMeta?.SidecarThresholdBytes ?? RepoLandscape.DefaultSidecarThreshold;
            long recordedBy = old.RecordedByUtcTicks; // `old` is only read here: every entry was recorded by then
            bool rootIsNetwork = Storage.NetworkPath.IsNetwork(root);   // once per update, not per file
            bool cacheDirIsNetwork = Storage.NetworkPath.IsNetwork(dir);

            using (var extractor = new TreeSitterSymbolExtractor())
            {
                // Update rebuilds the whole snapshot from the walk, so "record new state" is a dictionary
                // write and "drop" is a no-op (a dropped file is simply absent from the fresh snapshot).
                Action<string, FileState> upsert = (r, s) => newSnapshot[r] = s;
                Action<string> drop = _ => { };

                foreach (var file in walker.Walk(root))
                {
                    // A re-point/shutdown abandons the walk before anything is persisted: the loaded handles are
                    // ours to dispose, and the on-disk index stays exactly as it was.
                    if (ct.IsCancellationRequested) { text.Dispose(); symbols.Dispose(); ct.ThrowIfCancellationRequested(); }
                    if (onScan is not null && (++walked & 0x1FF) == 0) onScan(walked); // heartbeat every 512 files
                    var rel = file.RelativePath;
                    seen.Add(rel);
                    var mtime = file.MTimeTicks; // from the walk enumeration - avoids a per-file stat (a full
                                                 // extra round-trip per file over SMB; this is the no-op-update cost)
                    FileState? oldState = old.TryGetValue(rel, out var os) ? os : (FileState?)null;

                    // Cheap size+mtime pre-filter: unchanged -> keep the old state, no read. An entry recorded pending
                    // (negative) or unknown (0) - it could have been racy - never matches, nor does an entry from an older
                    // ledger whose stamp is too close to that ledger's write (LedgerTrust.MatchesListing).
                    if (oldState is { } u && LedgerTrust.MatchesListing(u, file.Size, mtime, recordedBy, rootIsNetwork))
                    {
                        newSnapshot[rel] = u;
                        continue;
                    }

                    switch (ApplyExistingFile(text, symbols, dir, sidecarThreshold, rel, file.FullPath, file.Size, mtime, oldState, recordedBy,
                                              rootIsNetwork, cacheDirIsNetwork, extractor, upsert, drop))
                    {
                        case ChangeKind.Added: added++; break;
                        case ChangeKind.Modified: modified++; break;
                        case ChangeKind.Removed: removed++; break;
                    }
                }
            }

            // Reconcile deletions ONLY when the walk was complete. A dropped directory (a transient SMB
            // failure, retried and still failed) makes its whole subtree ABSENT from this walk - which is
            // indistinguishable here from "the files were deleted". Pruning on that absence would delete a
            // healthy subtree from a good index on a passing network hiccup (a real over-the-wire data-loss
            // path). When the walk is incomplete we keep those paths (index + snapshot) untouched; a later
            // COMPLETE update reconciles any genuine deletions. Build already records DroppedDirs; Update must
            // honor it before pruning.
            if (walker.DroppedDirs == 0)
            {
                foreach (var rel in old.Keys)
                {
                    if (seen.Contains(rel)) continue;
                    text.RemovePath(rel);
                    symbols.RemovePath(rel);
                    PositionalSidecar.Delete(dir, rel); // idempotent (no-op if none) - covers any cutoff, no orphans
                    removed++;
                }
            }
            else
            {
                // Carry the un-seen (possibly just dropped) paths forward so the snapshot doesn't lose them
                // either - otherwise the next update's `old` wouldn't know to reconcile them once the walk heals.
                int retained = 0;
                foreach (var rel in old.Keys)
                    if (!seen.Contains(rel) && !newSnapshot.ContainsKey(rel) && old.TryGetValue(rel, out var st))
                    { newSnapshot[rel] = st; retained++; }
                Log.Global.Warn($"index update: walk dropped {walker.DroppedDirs} dir(s); SUPPRESSED deletion of {retained} absent path(s) to avoid pruning a subtree on a transient failure (a later complete update reconciles)");
            }

            if (added + modified + removed > RebuildThreshold)
                return FullRebuild(root, text, symbols, sw, ct);

            SaveAll(root, text, symbols, newSnapshot);
            sw.Stop();
            // Refresh coverage/meta. OverCapSkipped is exact from this walk (a size decision, free per file);
            // the symbol-skipped count needs file CONTENT to classify, which an incremental walk only has for
            // changed files, so carry forward the last full build's value (a full reindex refreshes it exactly).
            int carriedSymbolSkipped = priorMeta?.FilesSymbolSkipped ?? 0;
            // DroppedDirs is exact from THIS walk: if the update re-walked those dirs successfully it clears
            // the flag (the gap is closed); if they dropped again it stays flagged. Carry the build's adaptive
            // sidecar threshold + landscape forward unchanged (a full reindex is what re-measures the shape).
            IndexMetaFile.Write(root, text.DocumentCount, walker.OverCapSkipped, carriedSymbolSkipped, walker.DroppedDirs,
                                sidecarThreshold, priorMeta?.Landscape, walker.AmbiguousDirsSkipped,
                                contentVersion: IndexMetaFile.CarriedContentVersion(priorMeta)); // unchanged files weren't re-extracted
            return (text, symbols, new UpdateStats(added, modified, removed, sw.Elapsed.TotalSeconds, false));
        }
    }

    /// <summary>Cheaply prune paths the CURRENT ignore rules exclude but a STALE index (built by an older or
    /// looser version) still holds - a rival tool's <c>.claude/</c> cache dump, or a dir since added to
    /// CODECOMPASS_IGNORE. Unlike <see cref="Update"/> this does NO stat-walk and NO source reads: it
    /// enumerates the indexed paths and tombstones the now-ignored ones. Complements the query-time ignore
    /// filter (which already HIDES them) by physically shrinking the on-disk index so it stays clean without a
    /// full rebuild. Returns the freshly-opened handles (mutated) + how many paths were pruned; the caller
    /// swaps them in when >0. Pruned==0 (already clean, or no index) means the returned handles are unchanged.</summary>
    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, int Pruned) PruneIgnored(string root)
    {
        root = Path.GetFullPath(root);
        using var writeLock = IndexWriteLock.Acquire(IndexStore.GetCacheDir(root));
        if (!TryLoad(root, out var text, out var symbols)) return (text, symbols, 0);

        var ignore = new IgnoreRules();
        var prune = new HashSet<string>(text.AllPaths(), StringComparer.Ordinal);
        foreach (var p in symbols.AllPaths()) prune.Add(p);
        prune.RemoveWhere(p => !ignore.IsIgnoredPath(p));
        if (prune.Count == 0) return (text, symbols, 0);

        var dir = IndexStore.GetCacheDir(root);
        foreach (var rel in prune)
        {
            text.RemovePath(rel);
            symbols.RemovePath(rel);
            PositionalSidecar.Delete(dir, rel); // idempotent; drop any block sidecar for the pruned file too
        }
        text.Flush();
        symbols.Flush();
        // Refresh the doc count in meta; carry the other coverage fields forward unchanged (a prune doesn't
        // re-measure size caps / symbol skips / landscape).
        var pm = IndexMetaFile.Read(root);
        IndexMetaFile.Write(root, text.DocumentCount, pm?.FilesOverCap ?? 0, pm?.FilesSymbolSkipped ?? 0,
                            pm?.DroppedDirs ?? 0, pm?.SidecarThresholdBytes ?? RepoLandscape.DefaultSidecarThreshold, pm?.Landscape,
                            pm?.AmbiguousDirsSkipped ?? 0, contentVersion: IndexMetaFile.CarriedContentVersion(pm));
        return (text, symbols, prune.Count);
    }

    /// <summary>Disk-based targeted update: apply just the given changed paths.</summary>
    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, UpdateStats Stats) UpdatePaths(
        string root, IReadOnlyCollection<string> changedFullPaths, System.Threading.CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        using var writeLock = IndexWriteLock.Acquire(IndexStore.GetCacheDir(root), ct);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols, out var why)) { ThrowIfMustNotRebuild(root, why); return FullRebuild(root, text, symbols, sw, ct); }
        if (!TryLoadSnapshot(root, out var snapshot)) return FullRebuild(root, text, symbols, sw, ct);

        using (snapshot)
        {
            if (changedFullPaths.Count > RebuildThreshold)
                return FullRebuild(root, text, symbols, sw, ct);

            var counts = ApplyChanges(text, symbols, snapshot, root, changedFullPaths);
            SaveAll(root, text, symbols, snapshot);
            return (text, symbols, new UpdateStats(counts.Added, counts.Modified, counts.Removed, sw.Elapsed.TotalSeconds, false));
        }
    }

    /// <summary>
    /// Compact a repo's on-disk index (merge segments, drop tombstones) without re-reading source
    /// files - the cheap way to reclaim the segments/tombstones a long incremental session builds up.
    /// Falls back to a full build if no index exists yet. Caller owns/disposes the returned instances.
    /// </summary>
    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols) Compact(string root, System.Threading.CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        using var writeLock = IndexWriteLock.Acquire(IndexStore.GetCacheDir(root), ct);
        if (!TryLoad(root, out var text, out var symbols, out var why))
        {
            ThrowIfMustNotRebuild(root, why);
            var b = Build(root, ct: ct); // first-time compaction falls back to a full (cancellable) build
            return (b.Text, b.Symbols);
        }
        // Checked at the merge boundaries so a re-point/shutdown abandons the compaction (each index's on-disk
        // manifest stays internally consistent; a compacted text + not-yet-compacted symbols both load fine).
        // Dispose the loaded handles on cancel so a teardown doesn't leak the mmaps.
        try
        {
            ct.ThrowIfCancellationRequested();
            text.Compact();
            ct.ThrowIfCancellationRequested();
            symbols.Compact();
            return (text, symbols);
        }
        catch (OperationCanceledException) { text.Dispose(); symbols.Dispose(); throw; }
    }

    private static (SegmentedIndex, SegmentedSymbolIndex, UpdateStats) FullRebuild(
        string root, SegmentedIndex? oldText, SegmentedSymbolIndex? oldSymbols, Stopwatch sw,
        System.Threading.CancellationToken ct)
    {
        oldText?.Dispose();
        oldSymbols?.Dispose();
        var b = Build(root, ct: ct);
        sw.Stop();
        return (b.Text, b.Symbols, new UpdateStats(b.Stats.Files, 0, 0, b.Stats.Seconds, FullRebuild: true));
    }

    /// <summary>Apply changed paths to already-loaded in-memory indexes (does not persist).</summary>
    public static ChangeCounts ApplyChanges(
        SegmentedIndex text, SegmentedSymbolIndex symbols, DiskSnapshot snapshot,
        string root, IEnumerable<string> changedFullPaths)
    {
        root = Path.GetFullPath(root);
        CodeCompassConfig.Load(root);
        var dir = IndexStore.GetCacheDir(root);
        var ignore = new IgnoreRules();
        using var extractor = new TreeSitterSymbolExtractor();
        int added = 0, modified = 0, removed = 0;
        // Once per batch, not per changed file: the root's and cache dir's network-ness (each a handle open), the sidecar
        // cutoff the build recorded, and the ledger files' write-time bound (a stat per ledger file). The bound stays an
        // upper bound for every entry as the batch upserts: the snapshot's own last-upsert time is folded in per file.
        var batch = new ApplyBatch(IndexMetaFile.ReadFromCacheDir(dir)?.SidecarThresholdBytes ?? RepoLandscape.DefaultSidecarThreshold,
                                   Storage.NetworkPath.IsNetwork(root), Storage.NetworkPath.IsNetwork(dir), snapshot.RecordedByUtcTicks);

        foreach (var full in changedFullPaths.Select(Path.GetFullPath).Distinct())
        {
            string rel;
            try { rel = Path.GetRelativePath(root, full).Replace('\\', '/'); }
            catch { continue; }
            // Outside the repo: "../" escapes, "." is the root itself, and a different drive
            // (or a junction pointing off-root) yields a still-rooted path from GetRelativePath.
            if (PathSafety.IsOutsideRepo(rel)) continue;

            if (Directory.Exists(full))
            {
                foreach (var f in new FileWalker(ignore).Walk(full))
                {
                    var childRel = Path.GetRelativePath(root, f.FullPath).Replace('\\', '/');
                    ApplyFile(text, symbols, snapshot, dir, childRel, f.FullPath, ignore, extractor, batch,
                              ref added, ref modified, ref removed);
                }
            }
            else if (File.Exists(full))
            {
                ApplyFile(text, symbols, snapshot, dir, rel, full, ignore, extractor, batch,
                          ref added, ref modified, ref removed);
            }
            else
            {
                RemovePathAndChildren(text, symbols, snapshot, dir, rel, ref removed);
            }
        }

        return new ChangeCounts(added, modified, removed);
    }

    /// <summary>Persist in-memory indexes + snapshot to the cache. The caller must hold the cache's
    /// <see cref="IndexWriteLock"/> and must have reloaded if <see cref="IndexGeneration"/> moved since these
    /// handles were opened - flushing a stale manifest would drop another writer's segments.</summary>
    public static void Persist(string root, SegmentedIndex text, SegmentedSymbolIndex symbols,
                               DiskSnapshot snapshot) =>
        SaveAll(root, text, symbols, snapshot);

    private readonly record struct ApplyBatch(long SidecarThreshold, bool RootIsNetwork, bool CacheDirIsNetwork, long LedgerFilesRecordedBy);

    private static void ApplyFile(
        SegmentedIndex text, SegmentedSymbolIndex symbols, DiskSnapshot snapshot, string dir,
        string rel, string full, IgnoreRules ignore, TreeSitterSymbolExtractor extractor, ApplyBatch batch,
        ref int added, ref int modified, ref int removed)
    {
        FileState? oldState = snapshot.TryGetValue(rel, out var ps) ? ps : (FileState?)null;

        // These two guards are specific to the TARGETED path: a single path can become newly-ignored or
        // grow over the file cap, and must then be dropped. Build/Update never hit them because their
        // full walk pre-filters ignored and over-cap files.
        if (IsIgnoredRelPath(rel, ignore))
        {
            if (oldState is not null) { RemoveFromIndex(text, symbols, dir, rel); snapshot.Remove(rel); removed++; }
            return;
        }

        long size;
        try
        {
            var fi = new FileInfo(full);
            // A file that became a symlink (or was created as one) is never followed - same rule as the walk.
            if (FileWalker.IsLink(fi))
            {
                if (oldState is not null) { RemoveFromIndex(text, symbols, dir, rel); snapshot.Remove(rel); removed++; }
                return;
            }
            size = fi.Length;
        }
        catch { return; }
        if (size > ignore.MaxFileSizeBytes) // over the file cap -> not indexed (drop if we had it)
        {
            if (oldState is not null) { RemoveFromIndex(text, symbols, dir, rel); snapshot.Remove(rel); removed++; }
            return;
        }

        var mtime = File.GetLastWriteTimeUtc(full).Ticks; // targeted path has no walk record -> stat once
        long recordedBy = Math.Max(batch.LedgerFilesRecordedBy, snapshot.LastUpsertUtcTicks);
        switch (ApplyExistingFile(text, symbols, dir, batch.SidecarThreshold, rel, full, size, mtime, oldState, recordedBy,
                                  batch.RootIsNetwork, batch.CacheDirIsNetwork, extractor,
                                  (r, s) => snapshot[r] = s, r => snapshot.Remove(r)))
        {
            case ChangeKind.Added: added++; break;
            case ChangeKind.Modified: modified++; break;
            case ChangeKind.Removed: removed++; break;
        }
    }

    private enum ChangeKind { None, Added, Modified, Removed }

    /// <summary>
    /// Index ONE existing file against a LIVE index + snapshot. This is the single routine the two
    /// incremental callers share - <see cref="Update"/>'s per-file body and <see cref="ApplyFile"/> -
    /// so the read/classify/diff/mutate pipeline lives in one place instead of drifting across copies.
    /// Given the file's size + mtime and its previous state, it: reads and classifies (streamed-large vs
    /// whole-file vs binary/unreadable), skips a touched-but-identical file, and otherwise re-indexes it
    /// (removing any prior doc + sidecar first). The caller supplies <paramref name="upsert"/> (record the
    /// new snapshot state) and <paramref name="drop"/> (forget it), because the two callers use different
    /// snapshot models: Update rebuilds a fresh dictionary, ApplyFile mutates a <see cref="DiskSnapshot"/>
    /// in place. Returns what changed so the caller can count it.
    ///
    /// Build deliberately does NOT use this: it fills per-worker segment buffers in parallel with no
    /// diffing and its own RAM budget/watchdog, so folding it in would compromise the hot path.
    /// </summary>
    private static ChangeKind ApplyExistingFile(
        SegmentedIndex text, SegmentedSymbolIndex symbols, string dir, long sidecarThreshold,
        string rel, string full, long size, long mtime, FileState? oldState, long priorRecordedBy,
        bool network, bool cacheDirIsNetwork, TreeSitterSymbolExtractor extractor,
        Action<string, FileState> upsert, Action<string> drop)
    {
        // A binary-sentinel entry is in the ledger but was never a document.
        bool wasPresent = oldState is { IsBinary: false };
        long readStart = DateTime.UtcNow.Ticks;                        // before the bytes are read (LedgerTrust)
        // The read handle's identity just before and after the read (default = unknown): see LedgerTrust.Record.
        Storage.FileIdentity before = default, after = default;
        // A ledger on a share (CODECOMPASS_CACHE_DIR) may hold entries another machine recorded on its own clock.
        bool entryClockIsOurs = oldState is not { MTimeTicks: < 0 } || !cacheDirIsNetwork;
        FileState Rec(long recordedSize, string hash) => LedgerTrust.Record(
            new ReadStamp(size, mtime, before, after, readStart, DateTime.UtcNow.Ticks), network, oldState, priorRecordedBy, recordedSize, hash,
            entryClockIsOurs);

        // Large files: stream (bounded memory), trigrams only, no symbols.
        if (size >= LargeFileIndexer.StreamThresholdBytes)
        {
            before = Storage.FileIdentity.OfPath(full); // streamed through its own stream: attribute-only stats around it
            bool streamed = LargeFileIndexer.TryStreamIndex(full, out var big, out var len, out var bigHash, out var isBinary, out var blocks);
            after = Storage.FileIdentity.OfPath(full);
            if (!streamed)
            {
                // Binary/unreadable: drop it if it was indexed, so we never leave stale content behind. A binary file is
                // recorded as such so later updates skip it while it's unchanged.
                if (wasPresent) RemoveFromIndex(text, symbols, dir, rel);
                if (isBinary) upsert(rel, Rec(size, FileState.BinaryHash)); else drop(rel);
                return wasPresent ? ChangeKind.Removed : ChangeKind.None;
            }
            if (oldState?.ContentHash == bigHash) { upsert(rel, Rec(len, bigHash)); return ChangeKind.None; } // touched, identical
            text.RemovePath(rel);
            text.AddDocument(rel, big);
            symbols.RemovePath(rel); // over the symbol cap anyway; clear any stale symbols
            if (blocks is not null) PositionalSidecar.Write(dir, rel, blocks); else PositionalSidecar.Delete(dir, rel);
            upsert(rel, Rec(len, bigHash));
            return wasPresent ? ChangeKind.Modified : ChangeKind.Added;
        }

        byte[] bytes;
        try { bytes = Storage.FileIdentity.ReadAllBytes(full, out before, out after); }
        catch { if (oldState is { } keep) upsert(rel, keep); return ChangeKind.None; } // transient read error: keep as-was
        if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000))))
        {
            if (wasPresent) RemoveFromIndex(text, symbols, dir, rel);
            upsert(rel, Rec(bytes.Length, FileState.BinaryHash));
            return wasPresent ? ChangeKind.Removed : ChangeKind.None;
        }

        var hash = ContentHasher.Hash(bytes);
        if (oldState?.ContentHash == hash) { upsert(rel, Rec(bytes.Length, hash)); return ChangeKind.None; } // touched, identical

        var content = TextDecoder.FromBytes(bytes);
        text.RemovePath(rel);
        text.AddDocumentText(rel, content);
        symbols.RemovePath(rel);
        if (LanguageRegistry.ForPath(rel) is not null)
            symbols.AddForPath(rel, extractor.Extract(rel, content));

        // Reconcile the mid-size positional sidecar (mirror the build path): eligible + UTF-8 -> (re)write;
        // otherwise remove any stale one (shrank below the threshold, or became non-UTF-8). Write overwrites
        // a prior large-file sidecar at the same name, so a shrink from >=streaming to mid-size is handled too.
        if (bytes.Length >= sidecarThreshold && LargeFileIndexer.BuildBlocks(bytes) is { } mid)
            PositionalSidecar.Write(dir, rel, mid);
        else
            PositionalSidecar.Delete(dir, rel);

        upsert(rel, Rec(bytes.Length, hash));
        return wasPresent ? ChangeKind.Modified : ChangeKind.Added;
    }

    // Remove a file's doc, symbols, and any positional sidecar from a live index. Delete is idempotent
    // (no-op when absent), so it safely covers large, mid-size, and no-sidecar files alike.
    private static void RemoveFromIndex(SegmentedIndex text, SegmentedSymbolIndex symbols, string dir, string rel)
    {
        text.RemovePath(rel);
        symbols.RemovePath(rel);
        PositionalSidecar.Delete(dir, rel);
    }

    private static void RemovePathAndChildren(
        SegmentedIndex text, SegmentedSymbolIndex symbols, DiskSnapshot snapshot, string dir,
        string rel, ref int removed)
    {
        if (snapshot.TryGetValue(rel, out _) && snapshot.Remove(rel))
        {
            text.RemovePath(rel);
            symbols.RemovePath(rel);
            PositionalSidecar.Delete(dir, rel); // idempotent (no-op if none) - cutoff-independent, no orphans
            removed++;
        }

        var prefix = rel + "/";
        var children = snapshot.KeysWithPrefix(prefix).ToList();
        foreach (var k in children)
        {
            snapshot.Remove(k);
            text.RemovePath(k);
            symbols.RemovePath(k);
            PositionalSidecar.Delete(dir, k); // idempotent - covers any adaptive cutoff without orphaning
            removed++;
        }
    }

    private static bool IsIgnoredRelPath(string rel, IgnoreRules ignore)
    {
        var parts = rel.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (ignore.IsIgnoredDirectory(parts[i]))
                return true;
        return ignore.IsIgnoredFile(parts[^1], 0);
    }

    /// <summary>Cheap probe (file existence only, nothing opened): does <paramref name="root"/> have an index?</summary>
    public static bool HasIndex(string root)
    {
        try
        {
            var dir = IndexStore.CacheDirPath(Path.GetFullPath(root));
            return Directory.Exists(dir) && SegmentedIndex.Exists(dir) && SegmentedSymbolIndex.Exists(dir);
        }
        catch { return false; }
    }

    public static bool TryLoad(string root, out SegmentedIndex text, out SegmentedSymbolIndex symbols) =>
        TryLoad(root, out text, out symbols, out _);

    /// <summary>As <see cref="TryLoad(string, out SegmentedIndex, out SegmentedSymbolIndex)"/>, reporting WHY it failed:
    /// only Missing/Corrupt warrant a rebuild. A transient read failure (a sharing violation, an AV scanner, a share
    /// hiccup) must not trigger a full rebuild of a huge repo, and an index from a newer CodeCompass must not be rebuilt
    /// over (two installs would rebuild each other's index forever).</summary>
    public static bool TryLoad(string root, out SegmentedIndex text, out SegmentedSymbolIndex symbols, out IndexLoadFailure why)
    {
        root = Path.GetFullPath(root);
        text = null!;
        symbols = null!;
        why = IndexLoadFailure.None;
        var dir = IndexStore.GetCacheDir(root);
        try
        {
            if (!SegmentedIndex.Exists(dir) || !SegmentedSymbolIndex.Exists(dir)) { why = IndexLoadFailure.Missing; return false; }
            text = SegmentedIndex.Open(root, dir);
            symbols = SegmentedSymbolIndex.Open(dir);
            symbols.QueryIgnore = IgnoreRules.ForRoot(root);
            return true;
        }
        catch (Exception ex)
        {
            text?.Dispose();
            symbols?.Dispose();
            text = null!;
            symbols = null!;
            why = ex switch
            {
                IndexFormatTooNewException => IndexLoadFailure.NewerFormat,
                EndOfStreamException => IndexLoadFailure.Corrupt,          // a truncated file, not a busy one
                InvalidDataException or FormatException => IndexLoadFailure.Corrupt,
                FileNotFoundException or DirectoryNotFoundException => IndexLoadFailure.Transient, // vanished mid-open
                IOException or UnauthorizedAccessException => IndexLoadFailure.Transient,
                _ => IndexLoadFailure.Corrupt,
            };
            if (why != IndexLoadFailure.Corrupt) Log.For(root).Warn($"index load failed ({why}): {ex.Message}");
            return false;
        }
    }

    // The update paths rebuild from scratch only when there is no usable index; a transient read failure or a
    // newer-format index is an error to report, never a reason to discard and rebuild the whole thing.
    private static void ThrowIfMustNotRebuild(string root, IndexLoadFailure why)
    {
        if (why == IndexLoadFailure.Transient)
            throw new IOException($"the index for {root} could not be read right now (transient I/O); not rebuilding - retry shortly");
        if (why == IndexLoadFailure.NewerFormat)
            throw new InvalidOperationException($"the index for {root} was written by a newer CodeCompass - upgrade this install " +
                                                "(not rebuilding over it; run `codecompass index` to rebuild it deliberately)");
    }

    /// <summary>Open the change-detection snapshot for a repo (empty if none exists). Caller owns it.</summary>
    public static DiskSnapshot LoadSnapshot(string root) => DiskSnapshot.Open(IndexStore.GetCacheDir(root));

    private static bool TryLoadSnapshot(string root, out DiskSnapshot snapshot) =>
        DiskSnapshot.TryOpen(IndexStore.GetCacheDir(root), out snapshot);

    // Full-snapshot save (build / full reconcile): writes a fresh on-disk base.
    private static void SaveAll(string root, SegmentedIndex text, SegmentedSymbolIndex symbols,
                               IReadOnlyDictionary<string, FileState> snapshot)
    {
        text.Flush();
        symbols.Flush();
        SaveSnapshot(root, snapshot);
    }

    // Incremental save: journals the overlay (or compacts) without materializing the whole ledger.
    private static void SaveAll(string root, SegmentedIndex text, SegmentedSymbolIndex symbols,
                               DiskSnapshot snapshot)
    {
        text.Flush();
        symbols.Flush();
        snapshot.Save();
    }

    private static void SaveSnapshot(string root, IReadOnlyDictionary<string, FileState> snapshot) =>
        DiskSnapshot.WriteFullBase(IndexStore.GetCacheDir(root), snapshot);
}
