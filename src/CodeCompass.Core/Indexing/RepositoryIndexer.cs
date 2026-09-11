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
        int textSegCounter = SegmentedIndex.NextSegmentNumber(dir);
        int symSegCounter = SegmentedSymbolIndex.NextSegmentNumber(dir);
        long totalBytes = 0;
        long totalPostings = 0;
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
                // Hard ceiling from the whole-file-in-RAM design, independent of machine RAM: a file
                // is decoded into a single .NET string, which tops out near 1 GB of text (the UTF-16
                // buffer hits the 2 GB single-object limit). Above that the decode OOMs and would fail
                // the whole build, so skip it cleanly and loudly. (>2 GB files also fail File.ReadAll-
                // Bytes and are caught below; this catches the dangerous ~1-2 GB middle band.)
                if (file.Size > MaxTextFileBytes)
                {
                    Log.For(root).Info($"skipped {file.RelativePath}: {file.Size / 1048576.0:F0} MB exceeds the " +
                        $"~{MaxTextFileBytes / 1048576} MB single-file text limit (can't hold as one string) - " +
                        $"not indexed. Lower CODECOMPASS_MAX_FILE_MB to hide it, or exclude its directory.");
                    return worker;
                }

                // Reserve the real processing footprint, not just the raw size: while trigrams are
                // computed the raw bytes (1x) and the decoded UTF-16 string (2x) are both live, so
                // peak is ~3x the file size. Reserving 3x makes the budget's RAM-scaling honest -
                // "budget of N bytes" then genuinely fits a file up to ~N/3, and a bigger one reserves
                // the whole budget and runs solo (ByteBudget clamps the reservation, so no deadlock).
                long footprint = ProcessingFootprint(file.Size);
                reads.Acquire(footprint);
                int tid = Environment.CurrentManagedThreadId;
                inFlight[tid] = (file.RelativePath, file.Size, clock.ElapsedMilliseconds); // for the watchdog
                try
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(file.FullPath); }
                    catch (Exception ex) { Log.For(root).Debug($"skipped unreadable file {file.RelativePath}: {ex.Message}"); return worker; }
                    if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) return worker;

                    var content = TextDecoder.FromBytes(bytes);
                    var mtime = File.GetLastWriteTimeUtc(file.FullPath).Ticks;
                    var hash = ContentHasher.Hash(bytes);

                    var tg = TrigramIndex.ComputeTrigrams(content);
                    worker.Text.AddDocument(file.RelativePath, tg);
                    worker.TrigramPostings += tg.Length;
                    if (LanguageRegistry.ForPath(file.RelativePath) is not null)
                        foreach (var s in worker.Extractor.Extract(file.RelativePath, content))
                            worker.Symbols.Add(s);
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
        SaveSnapshot(root, snapshot);

        var stats = new IndexStats(text.DocumentCount, totalBytes, totalPostings,
                                   symbols.Count, sw.Elapsed.TotalSeconds, text.IndexBytes, cores);
        Log.For(root).Debug($"build stats: {stats.Files:N0} files, {stats.TrigramPostings:N0} trigram postings, " +
                            $"{stats.Symbols:N0} symbols, index {stats.IndexBytes / 1048576.0:F0} MB, " +
                            $"{cores} core(s), {stats.Seconds:F1}s");
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

    // Largest file we can decode into a single .NET string. A string's UTF-16 buffer hits the 2 GB
    // single-object limit near ~1 billion chars, so a source file past ~1 GB can't be held whole
    // regardless of RAM. Kept a touch under 1 GiB for headroom. (Indexing files larger than this would
    // need streaming/chunked trigram extraction that never materializes the whole file - not built.)
    private const long MaxTextFileBytes = 1000L * 1024 * 1024;

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
    }

    public static (SegmentedIndex Text, SegmentedSymbolIndex Symbols, UpdateStats Stats) Update(string root)
    {
        root = Path.GetFullPath(root);
        CodeCompassConfig.Load(root);
        var sw = Stopwatch.StartNew();

        if (!TryLoad(root, out var text, out var symbols)) return FullRebuild(root, text, symbols, sw);
        if (!TryLoadSnapshot(root, out var old)) return FullRebuild(root, text, symbols, sw);

        using (old)
        {
            var walker = new FileWalker(new IgnoreRules());
            var newSnapshot = new Dictionary<string, FileState>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int added = 0, modified = 0, removed = 0;

            using (var extractor = new TreeSitterSymbolExtractor())
            {
                foreach (var file in walker.Walk(root))
                {
                    var rel = file.RelativePath;
                    seen.Add(rel);
                    var mtime = File.GetLastWriteTimeUtc(file.FullPath).Ticks;

                    if (old.TryGetValue(rel, out var os) && os.Size == file.Size && os.MTimeTicks == mtime)
                    {
                        newSnapshot[rel] = os;
                        continue;
                    }

                    if (file.Size > MaxTextFileBytes)
                    {
                        Log.For(root).Info($"skipped {rel}: {file.Size / 1048576.0:F0} MB exceeds the " +
                            $"~{MaxTextFileBytes / 1048576} MB single-file text limit (can't hold as one string) - not indexed.");
                        continue;
                    }

                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(file.FullPath); }
                    catch { continue; }
                    if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;

                    var hash = ContentHasher.Hash(bytes);
                    if (old.TryGetValue(rel, out var os2) && os2.ContentHash == hash)
                    {
                        newSnapshot[rel] = new FileState(bytes.Length, mtime, hash);
                        continue;
                    }

                    var content = TextDecoder.FromBytes(bytes);
                    text.RemovePath(rel);
                    text.AddDocumentText(rel, content);
                    symbols.RemovePath(rel);
                    if (LanguageRegistry.ForPath(rel) is not null)
                        symbols.AddForPath(rel, extractor.Extract(rel, content));
                    newSnapshot[rel] = new FileState(bytes.Length, mtime, hash);
                    if (old.ContainsKey(rel)) modified++; else added++;
                }
            }

            foreach (var rel in old.Keys)
            {
                if (seen.Contains(rel)) continue;
                text.RemovePath(rel);
                symbols.RemovePath(rel);
                removed++;
            }

            if (added + modified + removed > RebuildThreshold)
                return FullRebuild(root, text, symbols, sw);

            SaveAll(root, text, symbols, newSnapshot);
            sw.Stop();
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
            if (rel.StartsWith("..", StringComparison.Ordinal) || rel == "." || Path.IsPathRooted(rel)) continue;

            if (Directory.Exists(full))
            {
                foreach (var f in new FileWalker(ignore).Walk(full))
                {
                    var childRel = Path.GetRelativePath(root, f.FullPath).Replace('\\', '/');
                    ApplyFile(text, symbols, snapshot, childRel, f.FullPath, ignore, extractor,
                              ref added, ref modified, ref removed);
                }
            }
            else if (File.Exists(full))
            {
                ApplyFile(text, symbols, snapshot, rel, full, ignore, extractor,
                          ref added, ref modified, ref removed);
            }
            else
            {
                RemovePathAndChildren(text, symbols, snapshot, rel, ref removed);
            }
        }

        return new ChangeCounts(added, modified, removed);
    }

    /// <summary>Persist in-memory indexes + snapshot to the cache.</summary>
    public static void Persist(string root, SegmentedIndex text, SegmentedSymbolIndex symbols,
                               DiskSnapshot snapshot) =>
        SaveAll(root, text, symbols, snapshot);

    private static void ApplyFile(
        SegmentedIndex text, SegmentedSymbolIndex symbols, DiskSnapshot snapshot,
        string rel, string full, IgnoreRules ignore, TreeSitterSymbolExtractor extractor,
        ref int added, ref int modified, ref int removed)
    {
        bool wasPresent = snapshot.ContainsKey(rel);

        if (IsIgnoredRelPath(rel, ignore))
        {
            if (wasPresent) { text.RemovePath(rel); symbols.RemovePath(rel); snapshot.Remove(rel); removed++; }
            return;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch { return; }

        if (bytes.Length > ignore.MaxFileSizeBytes ||
            bytes.Length > MaxTextFileBytes || // too big to decode as one string (see the constant)
            IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000))))
        {
            if (wasPresent) { text.RemovePath(rel); symbols.RemovePath(rel); snapshot.Remove(rel); removed++; }
            return;
        }

        var mtime = File.GetLastWriteTimeUtc(full).Ticks;
        var hash = ContentHasher.Hash(bytes);

        if (snapshot.TryGetValue(rel, out var old) && old.ContentHash == hash)
        {
            snapshot[rel] = new FileState(bytes.Length, mtime, hash);
            return;
        }

        var content = TextDecoder.FromBytes(bytes);
        text.RemovePath(rel);
        text.AddDocumentText(rel, content);
        symbols.RemovePath(rel);
        if (LanguageRegistry.ForPath(rel) is not null)
            symbols.AddForPath(rel, extractor.Extract(rel, content));
        snapshot[rel] = new FileState(bytes.Length, mtime, hash);

        if (wasPresent) modified++; else added++;
    }

    private static void RemovePathAndChildren(
        SegmentedIndex text, SegmentedSymbolIndex symbols, DiskSnapshot snapshot,
        string rel, ref int removed)
    {
        if (snapshot.Remove(rel))
        {
            text.RemovePath(rel);
            symbols.RemovePath(rel);
            removed++;
        }

        var prefix = rel + "/";
        var children = snapshot.KeysWithPrefix(prefix).ToList();
        foreach (var k in children)
        {
            snapshot.Remove(k);
            text.RemovePath(k);
            symbols.RemovePath(k);
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
