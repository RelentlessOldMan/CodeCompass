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

    /// <summary>Best-effort removal of sidecars not in <paramref name="keep"/> (the live large-file
    /// sidecar names), so a rebuild that drops a big file doesn't leave its sidecar behind.</summary>
    public static void CleanupOrphans(string dir, IReadOnlySet<string> keep) =>
        NumberedFiles.CleanupOrphans(dir, Pattern, keep);

    /// <summary>Scan a large candidate file using its sidecar: read only the blocks whose Blooms admit
    /// every query trigram. Returns true if the sidecar was present and usable (results filled up to
    /// maxResults); false means the caller should fall back to a whole-file scan.</summary>
    public static bool TryScan(string dir, string root, string rel, string query, List<SearchMatch> results, int maxResults)
    {
        var scPath = Path.Combine(dir, SidecarName(rel));
        if (!File.Exists(scPath)) return false;

        var qtris = TrigramIndex.ComputeTrigrams(query);
        if (qtris.Length == 0) return false; // query < 3 chars: no trigrams to filter on -> whole-file scan

        int bloomK, bomLen, count;
        int[] startLine;
        long[] startByte, endByte;
        byte[][] blooms;
        try
        {
            using var r = new BinaryReader(File.OpenRead(scPath), Encoding.UTF8, leaveOpen: false);
            if (r.ReadInt32() != Magic) return false;
            if (r.ReadString() != rel) return false; // hash collision guard
            int bloomBytes = r.ReadInt32();
            bloomK = r.ReadInt32();
            bomLen = r.ReadInt32();
            count = r.ReadInt32();
            if (count < 0 || bloomBytes <= 0 || bloomBytes > (16 << 20)) return false;
            startLine = new int[count];
            startByte = new long[count];
            endByte = new long[count];
            for (int i = 0; i < count; i++) { startLine[i] = r.ReadInt32(); startByte[i] = r.ReadInt64(); endByte[i] = r.ReadInt64(); }
            blooms = new byte[count][];
            for (int i = 0; i < count; i++) blooms[i] = r.ReadBytes(bloomBytes);
        }
        catch { return false; }

        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        FileStream? src = null;
        try
        {
            for (int i = 0; i < count && results.Count < maxResults; i++)
            {
                var bloom = new BloomFilter(blooms[i], bloomK);
                bool all = true;
                foreach (var t in qtris) if (!bloom.MayContain(t)) { all = false; break; }
                if (!all) continue;

                long blockLen = endByte[i] - startByte[i];
                if (blockLen <= 0 || blockLen > int.MaxValue) continue;
                src ??= new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = new byte[(int)blockLen];
                src.Seek(startByte[i], SeekOrigin.Begin);
                if (!ReadFull(src, bytes)) break;
                int skip = startByte[i] == 0 ? bomLen : 0;
                var text = Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
                FileScanner.ScanText(rel, text, query, results, maxResults, lineOffset: startLine[i] - 1);
            }
        }
        catch { /* partial results are acceptable; we still "handled" the file */ }
        finally { src?.Dispose(); }
        return true;
    }

    private static bool ReadFull(FileStream fs, byte[] buf)
    {
        int total = 0, r;
        while (total < buf.Length && (r = fs.Read(buf, total, buf.Length - total)) > 0) total += r;
        return total == buf.Length;
    }
}
