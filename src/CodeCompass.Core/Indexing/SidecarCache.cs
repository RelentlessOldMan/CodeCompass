using System.Text;

namespace CodeCompass.Core.Indexing;

/// <summary>
/// Parsed form of a positional sidecar (block table + per-block Bloom filters), ready to scan without
/// re-reading or re-parsing the file. Immutable after construction.
/// </summary>
internal sealed class ParsedSidecar
{
    public required string Rel;            // the repo-relative path this sidecar indexes (hash-collision guard)
    public required int BloomK;
    public required int BomLen;
    public required int Count;             // number of blocks
    public required int[] StartLine;
    public required long[] StartByte;
    public required long[] EndByte;
    public required byte[][] Blooms;       // per-block Bloom bytes
    public long ApproxBytes;               // resident cost, for the budget
    public long Length;                    // sidecar file length + mtime = the validity key
    public long MtimeTicks;
}

/// <summary>
/// A bounded, process-wide cache of parsed positional sidecars, keyed by sidecar path and validated by
/// (length, mtime). The long-lived MCP server otherwise re-reads and re-parses the ENTIRE Bloom payload of
/// every large-file candidate on every query - the field report measured ~695 MB of local sidecar reads
/// per broad query, 65% of all bytes. Caching turns that into read-once.
///
/// <para><b>Admission is scan-resistant (skip-when-full), NOT LRU-evict.</b> A single broad query's Bloom
/// working set can exceed the budget; a plain LRU filled by a scan bigger than its budget evicts every
/// entry before it is reused, so a repeated query hits NOTHING (the 1.0.169 default-budget bug: 256 MB
/// budget vs ~690 MB working set = 0 hits). Instead, once the cache is full we KEEP what is resident and
/// simply skip caching the overflow: a repeated oversized scan then hits whatever fit (stable resident set)
/// rather than thrashing to zero. Entries are only dropped when their file's (length,mtime) changes.</para>
///
/// <para>The default budget is RAM-scaled (RAM/16, clamped 256 MB..2 GB) so a normal repo's whole sidecar
/// set fits and every repeat query fully hits out of the box; <c>CODECOMPASS_SIDECAR_CACHE_MB</c> overrides.
/// <c>doctor</c> compares this budget to the index's total sidecar bytes so an over-budget repo is visible
/// (raise the env var) instead of silently under-caching. Tradeoff of skip-when-full: after the cache fills
/// it does not adapt to a shifted working set within one process; acceptable because the RAM-scaled default
/// holds typical repos whole, and it never regresses to the 0-hit thrash. A frequency policy (S3-FIFO) is
/// the upgrade if a >budget single working set with set-shift ever matters.</para>
/// </summary>
internal static class SidecarCache
{
    private const int Magic = 0x43435031; // "CCP1" - must match PositionalSidecar

    private static long _budget = ResolveBudget();

    private static long ResolveBudget()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_SIDECAR_CACHE_MB");
        if (long.TryParse(env, out var mb) && mb >= 0) return mb * 1024 * 1024;
        long total;
        try { total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { total = 8L * 1024 * 1024 * 1024; }
        if (total <= 0) total = 8L * 1024 * 1024 * 1024;
        // RAM/16, floor 256 MB, cap 2 GB. On a 32 GB box -> 2 GB, which holds a typical repo's whole sidecar
        // set (the field-report tree was ~1.4 GB) so repeat broad queries fully hit; small boxes stay modest.
        return Math.Clamp(total / 16, 256L * 1024 * 1024, 2L * 1024 * 1024 * 1024);
    }

    /// <summary>The resolved resident budget in bytes (for doctor / diagnostics).</summary>
    internal static long BudgetBytes => Interlocked.Read(ref _budget);

    private static readonly object _gate = new();
    private static readonly Dictionary<string, ParsedSidecar> _map = new(StringComparer.OrdinalIgnoreCase);
    private static long _residentBytes;

    internal static long ResidentBytes { get { lock (_gate) return _residentBytes; } }

    /// <summary>Parsed sidecar for <paramref name="scPath"/>, or null if it's missing/invalid/for another
    /// path. <paramref name="bytesRead"/> is the sidecar bytes actually read from disk THIS call: the file
    /// length on a cache miss, 0 on a cache hit - so the search trace can report the true I/O cost.</summary>
    public static ParsedSidecar? Get(string scPath, string rel, out long bytesRead)
    {
        bytesRead = 0;
        long len, mtime;
        try
        {
            var fi = new FileInfo(scPath);
            if (!fi.Exists) return null;
            len = fi.Length;
            mtime = fi.LastWriteTimeUtc.Ticks;
        }
        catch { return null; }

        // Fast path: a valid cached entry (same file bytes + mtime + intended path).
        lock (_gate)
        {
            if (_map.TryGetValue(scPath, out var sc) &&
                sc.Length == len && sc.MtimeTicks == mtime && string.Equals(sc.Rel, rel, StringComparison.Ordinal))
                return sc; // cache hit: no bytes read
        }

        // Miss: read + parse OUTSIDE the lock so concurrent first-queries don't serialize on I/O.
        var parsed = Parse(scPath, rel, len, mtime);
        if (parsed is null) return null;
        bytesRead = len;

        lock (_gate)
        {
            // Drop a stale entry for this path (file changed) before deciding whether to admit the fresh parse.
            if (_map.TryGetValue(scPath, out var existing))
            {
                if (existing.Length == len && existing.MtimeTicks == mtime)
                    return existing; // another thread parsed it meanwhile
                _residentBytes -= existing.ApproxBytes;
                _map.Remove(scPath);
            }
            // Scan-resistant admission: cache ONLY if it fits without evicting live entries. Overflow is
            // returned to the caller uncached - never evicted-to-admit (which would thrash an oversized scan
            // to zero hits). Keeps a stable resident set that repeat queries hit.
            if (_residentBytes + parsed.ApproxBytes <= _budget)
            {
                _map[scPath] = parsed;
                _residentBytes += parsed.ApproxBytes;
            }
            return parsed;
        }
    }

    private static ParsedSidecar? Parse(string scPath, string rel, long len, long mtime)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(scPath), Encoding.UTF8, leaveOpen: false);
            if (r.ReadInt32() != Magic) return null;
            if (r.ReadString() != rel) return null; // hash-collision guard
            int bloomBytes = r.ReadInt32();
            int bloomK = r.ReadInt32();
            int bomLen = r.ReadInt32();
            int count = r.ReadInt32();
            // Validate every trusted header field before use (a corrupt bloomK would make MayContain loop
            // forever; a bad bomLen would make GetString throw). Any bad header -> no sidecar, whole-file fallback.
            if (count < 0 || bloomBytes <= 0 || bloomBytes > (16 << 20) || bloomK < 1 || bloomK > 64 || bomLen < 0 || bomLen > 4)
                return null;
            var startLine = new int[count];
            var startByte = new long[count];
            var endByte = new long[count];
            for (int i = 0; i < count; i++) { startLine[i] = r.ReadInt32(); startByte[i] = r.ReadInt64(); endByte[i] = r.ReadInt64(); }
            var blooms = new byte[count][];
            for (int i = 0; i < count; i++) blooms[i] = r.ReadBytes(bloomBytes);
            return new ParsedSidecar
            {
                Rel = rel, BloomK = bloomK, BomLen = bomLen, Count = count,
                StartLine = startLine, StartByte = startByte, EndByte = endByte, Blooms = blooms,
                ApproxBytes = (long)count * bloomBytes + (long)count * 20 + 64,
                Length = len, MtimeTicks = mtime,
            };
        }
        catch { return null; }
    }

    /// <summary>Drop everything (a cache-clear).</summary>
    public static void Clear()
    {
        lock (_gate) { _map.Clear(); _residentBytes = 0; }
    }

    /// <summary>Test hook: reset the cache to empty with an explicit budget so a working-set-larger-than-budget
    /// scenario is deterministic without allocating gigabytes.</summary>
    internal static void ResetForTest(long budgetBytes)
    {
        lock (_gate) { _map.Clear(); _residentBytes = 0; Interlocked.Exchange(ref _budget, budgetBytes); }
    }
}
