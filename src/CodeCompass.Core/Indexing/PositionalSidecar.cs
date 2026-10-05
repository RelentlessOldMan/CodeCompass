using System.IO.Hashing;
using System.Text;
using CodeCompass.Core.Storage;

namespace CodeCompass.Core.Indexing;

/// <summary>
/// Per-large-file positional sidecar: the block table + per-block trigram Bloom filters produced by
/// <see cref="LargeFileIndexer"/>, stored in the (local) cache next to the segments. At query time it
/// lets a search read only the few blocks of a huge file whose Blooms admit all the query's trigrams,
/// instead of re-reading the whole file - the sidecar read is local and small; only the candidate
/// blocks are read from the (possibly networked) source. Missing/invalid sidecar => caller falls back
/// to a whole-file line scan, so it is an optimization, never a correctness dependency.
/// </summary>
public static class PositionalSidecar
{
    private const int Magic = 0x43435031; // "CCP1"
    public const string Pattern = "pos-*.bin";

    /// <summary>Sidecar filename for a repo-relative path (hashed, so it's a safe bare filename and
    /// stable across rebuilds). The path is also stored inside the file to guard against a hash clash.</summary>
    public static string SidecarName(string relPath) =>
        "pos-" + Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(relPath))) + ".bin";

    public static void Write(string dir, string relPath, LargeFileBlocks blocks)
    {
        AtomicFile.Write(Path.Combine(dir, SidecarName(relPath)), s =>
        {
            using var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
            w.Write(Magic);
            w.Write(relPath);
            w.Write(blocks.BloomBytes);
            w.Write(blocks.BloomK);
            w.Write(blocks.BomLen);
            w.Write(blocks.Blocks.Count);
            foreach (var b in blocks.Blocks) { w.Write(b.StartLine); w.Write(b.StartByte); w.Write(b.EndByte); }
            foreach (var b in blocks.Blocks) w.Write(b.Bloom);
        });
    }

    public static void Delete(string dir, string relPath) =>
        AtomicFile.TryDelete(Path.Combine(dir, SidecarName(relPath)));

    /// <summary>True if a (large-file) sidecar exists for this path. A purely LOCAL cache check: it lets
    /// search decide a candidate is a big file without a network <c>stat</c> on the source - a sidecar is
    /// written only for files at/over the streaming threshold. (No sidecar means either a normal file or,
    /// rarely, a large non-UTF-8 file that has no block index.)</summary>
    public static bool HasSidecar(string dir, string relPath) =>
        File.Exists(Path.Combine(dir, SidecarName(relPath)));

    /// <summary>Size on disk of a path's sidecar (header + block table + every block's Bloom), or 0 if none.
    /// This is the LOCAL byte cost <see cref="TryScan"/> pays up front per candidate to decide which blocks to
    /// read - surfaced so the search trace can separate it from the source bytes actually pulled.</summary>
    public static long SidecarLength(string dir, string relPath)
    {
        try { var fi = new FileInfo(Path.Combine(dir, SidecarName(relPath))); return fi.Exists ? fi.Length : 0; }
        catch { return 0; }
    }

    /// <summary>Best-effort removal of sidecars not in <paramref name="keep"/> (the live large-file
    /// sidecar names), so a rebuild that drops a big file doesn't leave its sidecar behind.</summary>
    public static void CleanupOrphans(string dir, IReadOnlySet<string> keep) =>
        NumberedFiles.CleanupOrphans(dir, Pattern, keep);

    /// <summary>Scan a large candidate file using its sidecar: read only the blocks whose Blooms admit the
    /// query's trigrams. <paramref name="caseSensitive"/> false probes every case variant of each trigram
    /// against the (case-sensitive) block Blooms and matches the block text case-insensitively - so a -i
    /// query still uses the block index instead of reading the whole multi-GB file. Returns true if the
    /// sidecar was present and usable (results filled up to maxResults); false => caller whole-file scans.</summary>
    public static bool TryScan(string dir, string root, string rel, string query, List<SearchMatch> results, int maxResults, bool caseSensitive = true)
        => TryScan(dir, root, rel, query, results, maxResults, caseSensitive, out _, out _);

    /// <summary>As <see cref="TryScan(string,string,string,string,List{SearchMatch},int,bool)"/>, also
    /// reporting how many bytes were read from the (possibly networked) source file - the sum of the
    /// candidate blocks' sizes. This is the metric the block index exists to minimize: a selective query
    /// reads a few ~1 MB blocks, not the whole multi-GB file. Tests assert it stays tiny relative to the
    /// file (verifying the network-cost win without needing a real share).</summary>
    public static bool TryScan(string dir, string root, string rel, string query, List<SearchMatch> results, int maxResults, out long bytesRead)
        => TryScan(dir, root, rel, query, results, maxResults, caseSensitive: true, out bytesRead, out _);

    public static bool TryScan(string dir, string root, string rel, string query, List<SearchMatch> results, int maxResults, bool caseSensitive, out long bytesRead)
        => TryScan(dir, root, rel, query, results, maxResults, caseSensitive, out bytesRead, out _);

    /// <param name="bytesRead">bytes pulled from the (possibly networked) SOURCE file - the candidate blocks.</param>
    /// <param name="sidecarBytesRead">LOCAL sidecar bytes read to pick those blocks THIS call: the sidecar's
    /// size on a cold read, 0 when the parsed sidecar was already cached in-process (see <see cref="SidecarCache"/>).</param>
    public static bool TryScan(string dir, string root, string rel, string query, List<SearchMatch> results, int maxResults, bool caseSensitive, out long bytesRead, out long sidecarBytesRead)
    {
        bytesRead = 0;
        sidecarBytesRead = 0;

        // Per-position trigram groups: one exact key each (case-sensitive) or all case variants (case-
        // insensitive). A block is admitted only if EVERY position has some variant in its Bloom.
        var groups = TrigramIndex.QueryTrigramGroups(query, caseSensitive);
        if (groups.Count == 0) return false; // query < 3 chars: no trigrams to filter on -> whole-file scan

        // The parsed block table + Blooms, cached in-process so the long-lived server doesn't re-read the
        // whole (~tens of MB) Bloom payload on every query. Missing/invalid/other-path sidecar -> whole-file fallback.
        var sc = SidecarCache.Get(Path.Combine(dir, SidecarName(rel)), rel, out sidecarBytesRead);
        if (sc is null) return false;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        FileStream? src = null;
        int startCount = results.Count;
        try
        {
            for (int i = 0; i < sc.Count && results.Count < maxResults; i++)
            {
                var bloom = new BloomFilter(sc.Blooms[i], sc.BloomK);
                bool all = true;
                foreach (var group in groups)
                {
                    bool any = false;
                    foreach (var t in group) if (bloom.MayContain(t)) { any = true; break; }
                    if (!any) { all = false; break; } // this position has no admitted variant -> skip block
                }
                if (!all) continue;

                long blockLen = sc.EndByte[i] - sc.StartByte[i];
                if (blockLen <= 0 || blockLen > int.MaxValue - LargeFileIndexer.SeamBytes) continue;
                // Read a little BEFORE the block too: if this block continues a line a forced cut split (no newline just
                // before it), a match straddling that cut is scanned here - its trigrams are in this block's Bloom.
                int tail = (int)Math.Min(LargeFileIndexer.SeamBytes, sc.StartByte[i]);
                src ??= new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = new byte[(int)blockLen + tail];
                src.Seek(sc.StartByte[i] - tail, SeekOrigin.Begin);
                // A short read means the file changed since it was indexed: not "handled" - the caller whole-file scans.
                if (!ReadFull(src, bytes)) return Abandon(results, startCount, rel, "file is shorter than when indexed");
                bytesRead += blockLen + tail; // bytes pulled from the (possibly networked) source
                if (tail == 0 || bytes[tail - 1] == (byte)'\n')
                {
                    int skip = sc.StartByte[i] == 0 ? sc.BomLen : 0;
                    var text = Encoding.UTF8.GetString(bytes, tail + skip, bytes.Length - tail - skip);
                    FileScanner.ScanText(rel, text, query, results, maxResults, lineOffset: sc.StartLine[i] - 1, comparison);
                }
                else ScanSeamBlock(rel, bytes, tail, query, results, maxResults, sc.StartLine[i], comparison);
            }
        }
        catch (Exception ex) { return Abandon(results, startCount, rel, ex.Message); }
        finally { src?.Dispose(); }
        return true;
    }

    // A read failed partway (a share hiccup, the file changed under us): discard this file's partial hits and report it
    // NOT handled, so the caller falls back to a whole-file scan. Returning "handled" with partial or zero hits was a
    // silent, nondeterministic refs/search gap.
    private static bool Abandon(List<SearchMatch> results, int startCount, string rel, string why)
    {
        if (results.Count > startCount) results.RemoveRange(startCount, results.Count - startCount);
        try { Diagnostics.Log.Global.Warn($"block-indexed scan of {rel} fell back to a whole-file scan: {why}"); } catch { }
        return false;
    }

    // Scan a block that continues a line split by a forced cut, from `tail` bytes before its start. Matches lying wholly
    // in the tail belong to the previous block (skipped); a match crossing into this block is the one only this scan can
    // find. Columns in a >16 MB line are reported relative to this block's start, as for any continuation block; a
    // straddling match reports column 1 (its LineTextOffset is shifted to match, so whole-word checks stay exact).
    private static void ScanSeamBlock(string rel, byte[] bytes, int tail, string query, List<SearchMatch> results,
                                      int maxResults, int startLine, StringComparison comparison)
    {
        var tailText = Encoding.UTF8.GetString(bytes, 0, tail);
        var combined = tailText + Encoding.UTF8.GetString(bytes, tail, bytes.Length - tail);
        int tailChars = tailText.Length;
        var found = new List<SearchMatch>();
        FileScanner.ScanText(rel, combined, query, found, int.MaxValue, lineOffset: startLine - 1, comparison);
        foreach (var m in found)
        {
            if (results.Count >= maxResults) return;
            if (m.Line != startLine) { results.Add(m); continue; }        // past a newline: an ordinary in-block hit
            int start = m.Column - 1;                                    // the first line starts at the tail's start
            if (start + query.Length <= tailChars) continue;             // wholly in the previous block
            int column = Math.Max(1, m.Column - tailChars);
            results.Add(m with { Column = column, LineTextOffset = m.LineTextOffset - (m.Column - column) });
        }
    }

    private static bool ReadFull(FileStream fs, byte[] buf)
    {
        int total = 0, r;
        while (total < buf.Length && (r = fs.Read(buf, total, buf.Length - total)) > 0) total += r;
        return total == buf.Length;
    }
}
