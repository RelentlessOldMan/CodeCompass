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
    public long ApproxBytes;               // resident cost, for the LRU budget
    public long Length;                    // sidecar file length + mtime = the validity key
    public long MtimeTicks;
}

/// <summary>
/// A bounded, process-wide LRU cache of parsed positional sidecars, keyed by sidecar path and validated by
/// (length, mtime). The long-lived MCP server otherwise re-reads and re-parses the ENTIRE Bloom payload of
/// every large-file candidate on every query - the field report measured ~695 MB of local sidecar reads
/// per broad query, 65% of all bytes, dominated by a few dozen big sidecars (a 1.2 GB file carries ~19 MB
/// of Blooms). Caching turns that into read-once: the first query pays one sequential read per sidecar,
/// every subsequent query reuses the parsed Blooms with zero I/O. Keyed by (length, mtime) so a rebuild /
/// incremental update that rewrites a sidecar is picked up automatically. Bounded so a huge repo can't grow
/// the cache without limit; least-recently-used sidecars are evicted past the budget.
/// </summary>
internal static class SidecarCache
{
    private const int Magic = 0x43435031; // "CCP1" - must match PositionalSidecar

    // Resident budget for cached Bloom payloads. The big sidecars that dominate the cost are a few dozen
    // files; a few hundred MB holds them. Override for tests / tuning via CODECOMPASS_SIDECAR_CACHE_MB.
    private static readonly long BudgetBytes = ResolveBudget();

    private static long ResolveBudget()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_SIDECAR_CACHE_MB");
        if (long.TryParse(env, out var mb) && mb >= 0) return mb * 1024 * 1024;
        return 256L * 1024 * 1024;
    }

    private sealed class Slot
    {
        public required string Key;         // sidecar path
        public required ParsedSidecar Sidecar;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, LinkedListNode<Slot>> _map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Slot> _lru = new(); // most-recently-used at the front
    private static long _residentBytes;

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
            if (_map.TryGetValue(scPath, out var node))
            {
                var sc = node.Value.Sidecar;
                if (sc.Length == len && sc.MtimeTicks == mtime && string.Equals(sc.Rel, rel, StringComparison.Ordinal))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return sc; // cache hit: no bytes read
                }
            }
        }

        // Miss: read + parse OUTSIDE the lock so concurrent first-queries don't serialize on I/O.
        var parsed = Parse(scPath, rel, len, mtime);
        if (parsed is null) return null;
        bytesRead = len;

        lock (_gate)
        {
            // Another thread may have inserted a valid entry meanwhile; prefer the existing one.
            if (_map.TryGetValue(scPath, out var existing))
            {
                var esc = existing.Value.Sidecar;
                if (esc.Length == len && esc.MtimeTicks == mtime)
                {
                    _lru.Remove(existing); _lru.AddFirst(existing);
                    return esc;
                }
                // stale entry for this path - drop it before inserting the fresh parse
                _residentBytes -= esc.ApproxBytes;
                _lru.Remove(existing);
                _map.Remove(scPath);
            }
            var slot = new LinkedListNode<Slot>(new Slot { Key = scPath, Sidecar = parsed });
            _lru.AddFirst(slot);
            _map[scPath] = slot;
            _residentBytes += parsed.ApproxBytes;
            EvictToBudget();
            return parsed;
        }
    }

    private static void EvictToBudget()
    {
        // caller holds _gate
        while (_residentBytes > BudgetBytes && _lru.Last is { } last)
        {
            _residentBytes -= last.Value.Sidecar.ApproxBytes;
            _map.Remove(last.Value.Key);
            _lru.RemoveLast();
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

    /// <summary>Drop everything (tests, or a cache-clear).</summary>
    public static void Clear()
    {
        lock (_gate) { _map.Clear(); _lru.Clear(); _residentBytes = 0; }
    }
}
