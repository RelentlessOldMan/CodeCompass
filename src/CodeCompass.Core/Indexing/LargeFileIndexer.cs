using System.Buffers;
using System.IO.Hashing;
using System.Text;
using CodeCompass.Core.Ignore;

namespace CodeCompass.Core.Indexing;

/// <summary>One ~1 MB, line-aligned block of a large file: enough to seek to it, know its first line
/// number, and (via the Bloom) tell whether a query's trigrams could occur inside it.</summary>
public sealed record BlockEntry(int StartLine, long StartByte, long EndByte, byte[] Bloom);

/// <summary>The per-file block/positional index (UTF-8 files only). Lets a search read just the few
/// candidate blocks of a huge file instead of re-reading the whole thing - the difference between a
/// few MB and multiple GB per query over a network share.</summary>
public sealed record LargeFileBlocks(int BloomBytes, int BloomK, int BomLen, IReadOnlyList<BlockEntry> Blocks);

/// <summary>
/// Indexes a file too large to hold whole in memory. A single .NET string caps near ~1 GB of text and
/// a single byte array at 2 GB, so the normal "read whole, decode to one string, compute trigrams"
/// path cannot handle multi-GB files regardless of RAM. This streams the file in chunks: it hashes
/// the raw bytes incrementally, decodes them incrementally, and accumulates the distinct trigram set
/// with a two-char carry across boundaries - peak memory is bounded by the chunk plus the trigram
/// set, not the file size. The trigram set is byte-for-byte identical to
/// <see cref="TrigramIndex.ComputeTrigrams"/> on the same text; the hash matches
/// <see cref="Text.ContentHasher"/> over the raw bytes.
///
/// For UTF-8 files it also builds a block/positional index (<see cref="LargeFileBlocks"/>): the file
/// is cut into line-aligned ~1 MB blocks, each carrying its first line number, byte range, and a
/// trigram Bloom filter, so a later search can skip to the handful of blocks that could contain the
/// query. Symbols are not extracted for streamed files (tree-sitter needs one in-memory string, and
/// such files are over the symbol cap anyway); they stay text-searchable.
/// </summary>
public static class LargeFileIndexer
{
    /// <summary>Files at or above this size take the streaming path; smaller files use the faster
    /// whole-file path. Below the ~1 GB single-string ceiling with wide margin.</summary>
    public const long StreamThresholdBytes = 128L * 1024 * 1024;

    private const int ChunkBytes = 1 << 20;                 // 1 MB read granularity
    private const int BlockTargetBytes = 1 << 20;           // ~1 MB per positional block (line-aligned)
    private const int BlockHardCapBytes = 16 << 20;         // force a cut if a single line is this long
    public const int BloomBytes = 16 * 1024;                // per-block Bloom (128k bits)
    public const int BloomK = 4;

    public static bool TryStreamIndex(string path, out long[] trigrams, out long byteLength, out string hashHex, out bool binary)
        => TryStreamIndex(path, out trigrams, out byteLength, out hashHex, out binary, out _);

    /// <summary>Stream-index a file. Returns false (never throws) if binary or unreadable. On success,
    /// <paramref name="blocks"/> is the positional index for UTF-8 files, or null (no positional index,
    /// e.g. UTF-16) - callers then fall back to a whole-file line scan at query time.</summary>
    public static bool TryStreamIndex(string path, out long[] trigrams, out long byteLength, out string hashHex,
                                      out bool binary, out LargeFileBlocks? blocks)
    {
        trigrams = Array.Empty<long>();
        byteLength = 0;
        hashHex = string.Empty;
        binary = false;
        blocks = null;

        var hasher = new XxHash128();
        byte[] buf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.SequentialScan);
            int fn = ReadChunk(fs, buf);
            if (fn == 0) { hashHex = Convert.ToHexString(hasher.GetCurrentHash()); return true; } // empty file
            hasher.Append(buf.AsSpan(0, fn));
            if (IgnoreRules.LooksBinary(buf.AsSpan(0, Math.Min(fn, 8000)))) { binary = true; return false; }

            var (enc, bomLen) = DetectBom(buf.AsSpan(0, fn));
            bool utf8 = ReferenceEquals(enc, Encoding.UTF8) || bomLen == 3 || bomLen == 0;

            var whole = new TrigramAccumulator();
            byteLength = fn;

            if (utf8)
            {
                var blockList = new List<BlockEntry>();
                using var blockBuf = new MemoryStream(BlockTargetBytes + ChunkBytes);
                long blockStartByte = 0;
                int blockStartLine = 0;

                blockBuf.Write(buf, 0, fn);
                DrainFullBlocks(blockBuf, whole, blockList, ref blockStartByte, ref blockStartLine, bomLen, force: false);

                int n;
                while ((n = ReadChunk(fs, buf)) > 0)
                {
                    hasher.Append(buf.AsSpan(0, n));
                    byteLength += n;
                    blockBuf.Write(buf, 0, n);
                    DrainFullBlocks(blockBuf, whole, blockList, ref blockStartByte, ref blockStartLine, bomLen, force: false);
                }
                DrainFullBlocks(blockBuf, whole, blockList, ref blockStartByte, ref blockStartLine, bomLen, force: true);

                blocks = new LargeFileBlocks(BloomBytes, BloomK, bomLen, blockList);
            }
            else
            {
                // Non-UTF-8 (e.g. UTF-16): trigrams only, no positional index (0x0A byte != newline).
                var decoder = enc.GetDecoder();
                char[] cbuf = Array.Empty<char>();
                DecodeInto(decoder, buf, bomLen, fn - bomLen, false, ref cbuf, whole);
                int n;
                while ((n = ReadChunk(fs, buf)) > 0)
                {
                    hasher.Append(buf.AsSpan(0, n));
                    byteLength += n;
                    DecodeInto(decoder, buf, 0, n, false, ref cbuf, whole);
                }
                DecodeInto(decoder, buf, 0, 0, true, ref cbuf, whole);
                if (cbuf.Length > 0) ArrayPool<char>.Shared.Return(cbuf);
            }

            hashHex = Convert.ToHexString(hasher.GetCurrentHash());
            trigrams = whole.ToArray();
            return true;
        }
        catch { return false; }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // Close every full (>= target, line-aligned) block currently buffered; on force, close whatever
    // remains. Each closed block feeds the whole-doc trigram accumulator (persistent carry, so cross-
    // block trigrams are kept) and gets its own Bloom over its own trigrams.
    private static void DrainFullBlocks(MemoryStream blockBuf, TrigramAccumulator whole, List<BlockEntry> blocks,
                                        ref long blockStartByte, ref int blockStartLine, int bomLen, bool force)
    {
        while (true)
        {
            int len = (int)blockBuf.Length;
            if (len == 0) return;
            byte[] data = blockBuf.GetBuffer(); // valid [0, len)

            int cut;
            if (len >= BlockTargetBytes)
            {
                cut = LastIndexOf(data, len, (byte)'\n') + 1; // cut just after the last newline
                if (cut <= 0) cut = len >= BlockHardCapBytes ? len : -1; // no newline: force only past the hard cap
            }
            else cut = force ? len : -1;
            if (cut <= 0) return; // nothing to close yet

            int skip = blockStartByte == 0 ? bomLen : 0; // strip BOM only at file start
            var chars = DecodeBlock(data, skip, cut - skip);
            whole.Add(chars);

            var bloom = BloomFilter.Create(BloomBytes, BloomK);
            AddTrigramsToBloom(chars, bloom);

            int newlines = Count(data, cut, (byte)'\n');
            blocks.Add(new BlockEntry(blockStartLine + 1, blockStartByte, blockStartByte + cut, bloom.Bits));
            blockStartByte += cut;
            blockStartLine += newlines;

            // Keep the remainder for the next block.
            int rem = len - cut;
            var tmp = rem > 0 ? data.AsSpan(cut, rem).ToArray() : Array.Empty<byte>();
            blockBuf.SetLength(0);
            if (rem > 0) blockBuf.Write(tmp, 0, rem);
            if (!force && rem < BlockTargetBytes) return;
            if (rem == 0) return;
        }
    }

    private static char[] DecodeBlock(byte[] bytes, int index, int count)
    {
        if (count <= 0) return Array.Empty<char>();
        var dec = Encoding.UTF8.GetDecoder();
        int need = dec.GetCharCount(bytes, index, count, flush: true);
        var chars = new char[need];
        dec.GetChars(bytes, index, count, chars, 0, flush: true);
        return chars;
    }

    private static void AddTrigramsToBloom(char[] chars, BloomFilter bloom)
    {
        for (int i = 0; i + 3 <= chars.Length; i++)
            bloom.Add(TrigramIndex.TriKey(chars[i], chars[i + 1], chars[i + 2]));
    }

    private static int LastIndexOf(byte[] a, int len, byte v)
    {
        for (int i = len - 1; i >= 0; i--) if (a[i] == v) return i;
        return -1;
    }

    private static int Count(byte[] a, int len, byte v)
    {
        int c = 0;
        for (int i = 0; i < len; i++) if (a[i] == v) c++;
        return c;
    }

    private static void DecodeInto(Decoder decoder, byte[] bytes, int index, int count, bool flush, ref char[] cbuf, TrigramAccumulator accum)
    {
        int need = decoder.GetCharCount(bytes, index, count, flush);
        if (need <= 0) return;
        if (cbuf.Length < need)
        {
            if (cbuf.Length > 0) ArrayPool<char>.Shared.Return(cbuf);
            cbuf = ArrayPool<char>.Shared.Rent(need);
        }
        int got = decoder.GetChars(bytes, index, count, cbuf, 0, flush);
        accum.Add(cbuf.AsSpan(0, got));
    }

    private static int ReadChunk(FileStream fs, byte[] buf)
    {
        int total = 0, r;
        while (total < buf.Length && (r = fs.Read(buf, total, buf.Length - total)) > 0) total += r;
        return total;
    }

    private static (Encoding Encoding, int BomLength) DetectBom(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: false), 4);
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false), 4);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return (Encoding.UTF8, 3);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return (Encoding.Unicode, 2);
        return (Encoding.UTF8, 0);
    }

    /// <summary>Accumulates the distinct trigram set from a stream of char blocks, carrying the last
    /// two chars across blocks so a trigram straddling a boundary is not missed. Same result as
    /// <see cref="TrigramIndex.ComputeTrigrams"/> over the concatenated text.</summary>
    private sealed class TrigramAccumulator
    {
        private readonly HashSet<long> _set = new();
        private char _a, _b;
        private int _seen;

        public void Add(ReadOnlySpan<char> chars)
        {
            char a = _a, b = _b;
            int seen = _seen;
            var set = _set;
            foreach (char c in chars)
            {
                if (seen >= 2) set.Add(TrigramIndex.TriKey(a, b, c));
                a = b; b = c;
                if (seen < 2) seen++;
            }
            _a = a; _b = b; _seen = seen;
        }

        public long[] ToArray()
        {
            var r = new long[_set.Count];
            _set.CopyTo(r);
            return r;
        }
    }
}
