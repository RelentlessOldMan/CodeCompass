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
        string root, Action<int, long>? onProgress = null)
    {
        root = Path.GetFullPath(root);
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
        long stallMs = stallSec * 1000L;
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
                string headline = stuck.Length > 0
                    ? $"indexing slow: {stuck.Length} worker(s) held one file >{stallSec}s at {curFiles:N0} files ({curBytes / 1048576.0:F0} MB) - a slow-to-parse file is grinding a worker while others may idle."
                    : $"indexing made no progress for ~{stallSec}s at {curFiles:N0} files ({curBytes / 1048576.0:F0} MB).";
                log.Warn(headline);
                if (held.Length == 0)
                    log.Warn("  (no files in flight - the stall is in a post-parse/finalize step, not a single file)");
                foreach (var kv in held.OrderByDescending(k => now - k.Value.StartMs))
                    log.Warn($"  worker {kv.Key}: {kv.Value.Path} ({kv.Value.Size / 1048576.0:F1} MB) held {(now - kv.Value.StartMs) / 1000.0:F0}s");
                log.Warn("If a file has been held many seconds it is the culprit: exclude it (CODECOMPASS_IGNORE) or " +
                         "lower CODECOMPASS_MAX_SYMBOL_MB / CODECOMPASS_MAX_FILE_MB.");

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
        var gate = new object();

        var sw = Stopwatch.StartNew();

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
        Parallel.ForEach(
            Partitioner.Create(walker.Walk(root), EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = cores },
            () => new BuildWorker(),
            (file, _, worker) =>
            {
                int tid = Environment.CurrentManagedThreadId;
                var mtime = file.MTimeTicks; // from the walk enumeration (no extra stat)

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
                        if (LargeFileIndexer.TryStreamIndex(file.FullPath, out var big, out var len, out var bigHash, out var bin, out var bigBlocks))
                        {
                            worker.Text.AddDocument(file.RelativePath, big);
                            worker.TrigramPostings += big.Length;
                            if (bigBlocks is not null)
                            {
                                PositionalSidecar.Write(dir, file.RelativePath, bigBlocks); // block index for cheap large-file search
                                posSidecars.Add(PositionalSidecar.SidecarName(file.RelativePath));
                            }
                            worker.Snapshot[file.RelativePath] = new FileState(len, mtime, bigHash);
                            worker.Bytes += len;
                            // Streamed files are text-searchable but get no symbols (tree-sitter needs the
                            // whole string). If it's a symbol-bearing language, count it so a go-to-definition
                            // zero can disclose that a definition might live here.
                            if (LanguageRegistry.ForPath(file.RelativePath) is not null) worker.SymbolSkipped++;
                            Interlocked.Increment(ref progressFiles);
                            Interlocked.Add(ref progressBytes, len);
                            if (worker.Text.ApproxBytes >= textBudget) FlushText(worker, dir, ref textSegCounter, textSegFiles);
                        }
                        else if (!bin)
                            Log.For(root).Debug($"skipped unreadable large file {file.RelativePath}");
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
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(file.FullPath); }
                    catch (Exception ex) { Log.For(root).Debug($"skipped unreadable file {file.RelativePath}: {ex.Message}"); return worker; }
                    if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) return worker;

                    var content = TextDecoder.FromBytes(bytes);
                    var hash = ContentHasher.Hash(bytes);

                    var tg = TrigramIndex.ComputeTrigrams(content);
                    worker.Text.AddDocument(file.RelativePath, tg);
                    worker.TrigramPostings += tg.Length;
                    if (LanguageRegistry.ForPath(file.RelativePath) is not null)
                    {
                        // A symbol-bearing file whose symbols are skipped by a cap / data-blob guard is
                        // counted (not parsed) so a go-to-definition zero can be honest about it.
                        if (worker.Extractor.WouldSkipSymbols(file.RelativePath, content)) worker.SymbolSkipped++;
                        else foreach (var s in worker.Extractor.Extract(file.RelativePath, content)) worker.Symbols.Add(s);
                    }
                    worker.Snapshot[file.RelativePath] = new FileState(bytes.Length, mtime, hash);
                    worker.Bytes += bytes.Length;
                    Interlocked.Increment(ref progressFiles);
                    Interlocked.Add(ref progressBytes, bytes.Length);

                    if (worker.Text.ApproxBytes >= textBudget) FlushText(worker, dir, ref textSegCounter, textSegFiles);
                    if (worker.Symbols.ApproxBytes >= symBudget) FlushSymbols(worker, dir, ref symSegCounter, symSegFiles);
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
                }
                worker.Extractor.Dispose();
            });

        sw.Stop();
        progressTimer?.Dispose();
        buildDone.Set();           // stop the watchdog thread
        watchdog.Join(1000);
        buildDone.Dispose();
        onProgress?.Invoke(progressFiles, progressBytes);

        if (walker.OverCapSkipped > 0)
        {
            var largest = walker.LargestOverCapPath is null ? "" :
                $" (largest: {Path.GetFileName(walker.LargestOverCapPath)} {walker.LargestOverCapBytes / 1048576.0:F0} MB)";
            Log.For(root).Info($"skipped {walker.OverCapSkipped:N0} file(s) over the " +
                $"{ignore.MaxFileSizeBytes / 1048576.0:F0} MB size cap{largest} - not in the index. " +
                $"Raise CODECOMPASS_MAX_FILE_MB to include them.");
        }

        var textOrdered = textSegFiles.OrderBy(x => x.Num).Select(x => x.Name).ToList();
        var symOrdered = symSegFiles.OrderBy(x => x.Num).Select(x => x.Name).ToList();
        var text = SegmentedIndex.FromSegmentFiles(root, dir, textOrdered, textSegCounter, textBudget);
        var symbols = SegmentedSymbolIndex.FromSegmentFiles(dir, symOrdered, symSegCounter, symBudget);
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
        IndexMetaFile.Write(root, text.DocumentCount, walker.OverCapSkipped, totalSymbolSkipped);
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
        public long Bytes;
        public long TrigramPostings; // sum of per-doc distinct-trigram counts (deterministic total)
        public int SymbolSkipped;    // symbol-language files indexed for text but with NO symbols extracted
    }

    /// <param name="onScan">Optional heartbeat: invoked with the running count of files stat-walked,
    /// so a caller can show progress during the (silent, potentially slow over a network share)
    /// change-detection pass.</param>
    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, UpdateStats Stats) Update(string root, Action<int>? onScan = null)
    {
        root = Path.GetFullPath(root);
        CodeCompassConfig.Load(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols)) return FullRebuild(root, text, symbols, sw);
        if (!TryLoadSnapshot(root, out var old)) return FullRebuild(root, text, symbols, sw);

        using (old)
        {
            var dir = IndexStore.GetCacheDir(root);
            var walker = new FileWalker(new IgnoreRules());
            var newSnapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int added = 0, modified = 0, removed = 0, walked = 0;

            using (var extractor = new TreeSitterSymbolExtractor())
            {
                // Update rebuilds the whole snapshot from the walk, so "record new state" is a dictionary
                // write and "drop" is a no-op (a dropped file is simply absent from the fresh snapshot).
                Action<string, FileState> upsert = (r, s) => newSnapshot[r] = s;
                Action<string> drop = _ => { };

                foreach (var file in walker.Walk(root))
                {
                    if (onScan is not null && (++walked & 0x1FF) == 0) onScan(walked); // heartbeat every 512 files
                    var rel = file.RelativePath;
                    seen.Add(rel);
                    var mtime = file.MTimeTicks; // from the walk enumeration - avoids a per-file stat (a full
                                                 // extra round-trip per file over SMB; this is the no-op-update cost)
                    FileState? oldState = old.TryGetValue(rel, out var os) ? os : (FileState?)null;

                    // Cheap size+mtime pre-filter: unchanged -> keep the old state, no read.
                    if (oldState is { } u && u.Size == file.Size && u.MTimeTicks == mtime)
                    {
                        newSnapshot[rel] = u;
                        continue;
                    }

                    switch (ApplyExistingFile(text, symbols, dir, rel, file.FullPath, file.Size, mtime, oldState, extractor, upsert, drop))
                    {
                        case ChangeKind.Added: added++; break;
                        case ChangeKind.Modified: modified++; break;
                        case ChangeKind.Removed: removed++; break;
                    }
                }
            }

            foreach (var rel in old.Keys)
            {
                if (seen.Contains(rel)) continue;
                text.RemovePath(rel);
                symbols.RemovePath(rel);
                if (old.TryGetValue(rel, out var gone) && gone.Size >= LargeFileIndexer.StreamThresholdBytes)
                    PositionalSidecar.Delete(dir, rel); // drop a removed large file's sidecar
                removed++;
            }

            if (added + modified + removed > RebuildThreshold)
                return FullRebuild(root, text, symbols, sw);

            SaveAll(root, text, symbols, newSnapshot);
            sw.Stop();
            // Refresh coverage/meta. OverCapSkipped is exact from this walk (a size decision, free per file);
            // the symbol-skipped count needs file CONTENT to classify, which an incremental walk only has for
            // changed files, so carry forward the last full build's value (a full reindex refreshes it exactly).
            int carriedSymbolSkipped = IndexMetaFile.Read(root)?.FilesSymbolSkipped ?? 0;
            IndexMetaFile.Write(root, text.DocumentCount, walker.OverCapSkipped, carriedSymbolSkipped);
            return (text, symbols, new UpdateStats(added, modified, removed, sw.Elapsed.TotalSeconds, false));
        }
    }

    /// <summary>Disk-based targeted update: apply just the given changed paths.</summary>
    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, UpdateStats Stats) UpdatePaths(
        string root, IReadOnlyCollection<string> changedFullPaths)
    {
        root = Path.GetFullPath(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols)) return FullRebuild(root, text, symbols, sw);
        if (!TryLoadSnapshot(root, out var snapshot)) return FullRebuild(root, text, symbols, sw);

        using (snapshot)
        {
            if (changedFullPaths.Count > RebuildThreshold)
                return FullRebuild(root, text, symbols, sw);

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
    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols) Compact(string root)
    {
        root = Path.GetFullPath(root);
        if (!TryLoad(root, out var text, out var symbols))
        {
            var b = Build(root);
            return (b.Text, b.Symbols);
        }
        text.Compact();
        symbols.Compact();
        return (text, symbols);
    }

    private static (SegmentedIndex, SegmentedSymbolIndex, UpdateStats) FullRebuild(
        string root, SegmentedIndex? oldText, SegmentedSymbolIndex? oldSymbols, Stopwatch sw)
    {
        oldText?.Dispose();
        oldSymbols?.Dispose();
        var b = Build(root);
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
                    ApplyFile(text, symbols, snapshot, dir, childRel, f.FullPath, ignore, extractor,
                              ref added, ref modified, ref removed);
                }
            }
            else if (File.Exists(full))
            {
                ApplyFile(text, symbols, snapshot, dir, rel, full, ignore, extractor,
                          ref added, ref modified, ref removed);
            }
            else
            {
                RemovePathAndChildren(text, symbols, snapshot, dir, rel, ref removed);
            }
        }

        return new ChangeCounts(added, modified, removed);
    }

    /// <summary>Persist in-memory indexes + snapshot to the cache.</summary>
    public static void Persist(string root, SegmentedIndex text, SegmentedSymbolIndex symbols,
                               DiskSnapshot snapshot) =>
        SaveAll(root, text, symbols, snapshot);

    private static void ApplyFile(
        SegmentedIndex text, SegmentedSymbolIndex symbols, DiskSnapshot snapshot, string dir,
        string rel, string full, IgnoreRules ignore, TreeSitterSymbolExtractor extractor,
        ref int added, ref int modified, ref int removed)
    {
        FileState? oldState = snapshot.TryGetValue(rel, out var ps) ? ps : (FileState?)null;
        bool wasLarge = oldState is { } o && o.Size >= LargeFileIndexer.StreamThresholdBytes;

        // These two guards are specific to the TARGETED path: a single path can become newly-ignored or
        // grow over the file cap, and must then be dropped. Build/Update never hit them because their
        // full walk pre-filters ignored and over-cap files.
        if (IsIgnoredRelPath(rel, ignore))
        {
            if (oldState is not null) { RemoveFromIndex(text, symbols, dir, rel, wasLarge); snapshot.Remove(rel); removed++; }
            return;
        }

        long size;
        try { size = new FileInfo(full).Length; }
        catch { return; }
        if (size > ignore.MaxFileSizeBytes) // over the file cap -> not indexed (drop if we had it)
        {
            if (oldState is not null) { RemoveFromIndex(text, symbols, dir, rel, wasLarge); snapshot.Remove(rel); removed++; }
            return;
        }

        var mtime = File.GetLastWriteTimeUtc(full).Ticks; // targeted path has no walk record -> stat once
        switch (ApplyExistingFile(text, symbols, dir, rel, full, size, mtime, oldState, extractor,
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
        SegmentedIndex text, SegmentedSymbolIndex symbols, string dir,
        string rel, string full, long size, long mtime, FileState? oldState,
        TreeSitterSymbolExtractor extractor,
        Action<string, FileState> upsert, Action<string> drop)
    {
        bool wasPresent = oldState is not null;
        bool wasLarge = oldState is { } os0 && os0.Size >= LargeFileIndexer.StreamThresholdBytes;

        // Large files: stream (bounded memory), trigrams only, no symbols.
        if (size >= LargeFileIndexer.StreamThresholdBytes)
        {
            if (!LargeFileIndexer.TryStreamIndex(full, out var big, out var len, out var bigHash, out _, out var blocks))
            {
                // Binary/unreadable: drop it if it was indexed, so we never leave stale content behind.
                if (wasPresent) { RemoveFromIndex(text, symbols, dir, rel, wasLarge); drop(rel); return ChangeKind.Removed; }
                return ChangeKind.None;
            }
            if (oldState?.ContentHash == bigHash) { upsert(rel, new FileState(len, mtime, bigHash)); return ChangeKind.None; } // touched, identical
            text.RemovePath(rel);
            text.AddDocument(rel, big);
            symbols.RemovePath(rel); // over the symbol cap anyway; clear any stale symbols
            if (blocks is not null) PositionalSidecar.Write(dir, rel, blocks); else PositionalSidecar.Delete(dir, rel);
            upsert(rel, new FileState(len, mtime, bigHash));
            return wasPresent ? ChangeKind.Modified : ChangeKind.Added;
        }

        if (wasLarge) PositionalSidecar.Delete(dir, rel); // shrank below the streaming threshold

        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch { if (oldState is { } keep) upsert(rel, keep); return ChangeKind.None; } // transient read error: keep as-was
        if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000))))
        {
            if (wasPresent) { RemoveFromIndex(text, symbols, dir, rel, large: false); drop(rel); return ChangeKind.Removed; }
            return ChangeKind.None;
        }

        var hash = ContentHasher.Hash(bytes);
        if (oldState?.ContentHash == hash) { upsert(rel, new FileState(bytes.Length, mtime, hash)); return ChangeKind.None; } // touched, identical

        var content = TextDecoder.FromBytes(bytes);
        text.RemovePath(rel);
        text.AddDocumentText(rel, content);
        symbols.RemovePath(rel);
        if (LanguageRegistry.ForPath(rel) is not null)
            symbols.AddForPath(rel, extractor.Extract(rel, content));
        upsert(rel, new FileState(bytes.Length, mtime, hash));
        return wasPresent ? ChangeKind.Modified : ChangeKind.Added;
    }

    // Remove a file's doc, symbols, and (if it was a large file) its positional sidecar from a live index.
    private static void RemoveFromIndex(SegmentedIndex text, SegmentedSymbolIndex symbols, string dir, string rel, bool large)
    {
        text.RemovePath(rel);
        symbols.RemovePath(rel);
        if (large) PositionalSidecar.Delete(dir, rel);
    }

    private static void RemovePathAndChildren(
        SegmentedIndex text, SegmentedSymbolIndex symbols, DiskSnapshot snapshot, string dir,
        string rel, ref int removed)
    {
        if (snapshot.TryGetValue(rel, out var st) && snapshot.Remove(rel))
        {
            text.RemovePath(rel);
            symbols.RemovePath(rel);
            if (st.Size >= LargeFileIndexer.StreamThresholdBytes) PositionalSidecar.Delete(dir, rel);
            removed++;
        }

        var prefix = rel + "/";
        var children = snapshot.KeysWithPrefix(prefix).ToList();
        foreach (var k in children)
        {
            bool big = snapshot.TryGetValue(k, out var cs) && cs.Size >= LargeFileIndexer.StreamThresholdBytes;
            snapshot.Remove(k);
            text.RemovePath(k);
            symbols.RemovePath(k);
            if (big) PositionalSidecar.Delete(dir, k);
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

    public static bool TryLoad(string root, out SegmentedIndex text, out SegmentedSymbolIndex symbols)
    {
        root = Path.GetFullPath(root);
        text = null!;
        symbols = null!;
        var dir = IndexStore.GetCacheDir(root);
        try
        {
            if (!SegmentedIndex.Exists(dir) || !SegmentedSymbolIndex.Exists(dir)) return false;
            text = SegmentedIndex.Open(root, dir);
            symbols = SegmentedSymbolIndex.Open(dir);
            return true;
        }
        catch
        {
            text?.Dispose();
            symbols?.Dispose();
            text = null!;
            symbols = null!;
            return false;
        }
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
