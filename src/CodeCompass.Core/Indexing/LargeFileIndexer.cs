using System.Buffers;
using System.IO.Hashing;
using System.Text;
using CodeCompass.Core.Ignore;

namespace CodeCompass.Core.Indexing;

/// <summary>
/// Indexes a file too large to hold whole in memory. A single .NET string caps near ~1 GB of text
/// and a single byte array at 2 GB, so the normal "read the whole file, decode to one string, compute
/// trigrams" path cannot handle multi-GB files regardless of machine RAM. This streams the file in
/// chunks instead: it hashes the raw bytes incrementally, decodes them incrementally (BOM-aware, to
/// match <see cref="Text.TextDecoder"/>), and accumulates the distinct trigram set with a two-char
/// carry across chunk boundaries - so peak memory is bounded by the chunk size plus the distinct-
/// trigram set, not by the file size. The trigram set it produces is byte-for-byte identical to
/// <see cref="TrigramIndex.ComputeTrigrams"/> on the same decoded text; the hash matches
/// <see cref="Text.ContentHasher"/> over the raw bytes.
///
/// Symbols are not extracted for streamed files: tree-sitter parses a single in-memory string (same
/// ~1 GB ceiling), and such files are over the symbol cap anyway. They remain fully text-searchable.
/// </summary>
public static class LargeFileIndexer
{
    /// <summary>Files at or above this size take the streaming path (both when indexing and when a
    /// search verifies a match); smaller files use the faster whole-file path. Below the ~1 GB
    /// single-string ceiling with wide margin, and above any normal source file.</summary>
    public const long StreamThresholdBytes = 128L * 1024 * 1024;

    private const int ChunkBytes = 1 << 20; // 1 MB read granularity

    /// <summary>Stream-index a file. Returns false (and does not throw) if the file is binary or
    /// unreadable. On success, <paramref name="trigrams"/> is the distinct trigram set,
    /// <paramref name="byteLength"/> the raw size, and <paramref name="hashHex"/> the content hash.</summary>
    public static bool TryStreamIndex(string path, out long[] trigrams, out long byteLength, out string hashHex, out bool binary)
    {
        trigrams = Array.Empty<long>();
        byteLength = 0;
        hashHex = string.Empty;
        binary = false;

        var hasher = new XxHash128();
        var accum = new TrigramAccumulator();
        byte[] bbuf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        char[] cbuf = Array.Empty<char>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.SequentialScan);
            Decoder? decoder = null;
            bool first = true;
            int n;
            while ((n = ReadChunk(fs, bbuf)) > 0)
            {
                hasher.Append(bbuf.AsSpan(0, n));
                byteLength += n;

                int offset = 0;
                if (first)
                {
                    first = false;
                    if (IgnoreRules.LooksBinary(bbuf.AsSpan(0, Math.Min(n, 8000))))
                    {
                        binary = true;
                        return false;
                    }
                    var (enc, bomLen) = DetectBom(bbuf.AsSpan(0, n));
                    decoder = enc.GetDecoder();
                    offset = bomLen;
                }

                int byteCount = n - offset;
                if (byteCount > 0)
                    DecodeInto(decoder!, bbuf, offset, byteCount, flush: false, ref cbuf, accum);
            }

            if (decoder is not null)
                DecodeInto(decoder, bbuf, 0, 0, flush: true, ref cbuf, accum); // emit any trailing state

            hashHex = Convert.ToHexString(hasher.GetCurrentHash());
            trigrams = accum.ToArray();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bbuf);
            if (cbuf.Length > 0) ArrayPool<char>.Shared.Return(cbuf);
        }
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

    // Fill the buffer as much as possible; a single FileStream.Read may return short. Returns total
    // bytes read (0 at EOF). Larger chunks keep decode/hash overhead down but are not required for
    // correctness (the Decoder carries state across calls).
    private static int ReadChunk(FileStream fs, byte[] buf)
    {
        int total = 0, r;
        while (total < buf.Length && (r = fs.Read(buf, total, buf.Length - total)) > 0) total += r;
        return total;
    }

    // Mirror StreamReader(Encoding.UTF8, detectEncodingFromByteOrderMarks:true): default UTF-8, but
    // switch on a recognized byte-order mark. Returns the encoding and the BOM length to skip when
    // decoding (the BOM bytes are still hashed, matching ContentHasher over the raw file).
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

    /// <summary>Accumulates the distinct set of trigrams from a stream of char blocks, carrying the
    /// last two chars across blocks so a trigram straddling a boundary is not missed. Produces the
    /// same set as <see cref="TrigramIndex.ComputeTrigrams"/> over the concatenated text.</summary>
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
                a = b;
                b = c;
                if (seen < 2) seen++;
            }
            _a = a;
            _b = b;
            _seen = seen;
        }

        public long[] ToArray()
        {
            var r = new long[_set.Count];
            _set.CopyTo(r);
            return r;
        }
    }
}
