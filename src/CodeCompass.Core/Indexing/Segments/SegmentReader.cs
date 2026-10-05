using CodeCompass.Core.Storage;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace CodeCompass.Core.Indexing.Segments;

/// <summary>
/// Reads an immutable segment via a memory-mapped file. Posting lists and paths are read
/// on demand, so only the pages a query actually touches become resident - the whole
/// index never has to fit in RAM.
/// </summary>
public sealed class SegmentReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _termKeysOff, _termInfoOff, _postingsOff, _docTableOff;

    public int DocCount { get; }
    public int TermCount { get; }
    public string FilePath { get; }

    public SegmentReader(string filePath)
    {
        FilePath = filePath;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        if (_view.ReadUInt32(0) != SegmentBuilder.Magic) throw new InvalidDataException("not a CodeCompass segment");
        int version = _view.ReadInt32(4);
        if (version > SegmentBuilder.Version) throw new IndexFormatTooNewException("segment", version, SegmentBuilder.Version);
        if (version != SegmentBuilder.Version) throw new InvalidDataException("unsupported segment version");
        DocCount = _view.ReadInt32(8);
        TermCount = _view.ReadInt32(12);
        _termKeysOff = _view.ReadInt64(16);
        _termInfoOff = _view.ReadInt64(24);
        _postingsOff = _view.ReadInt64(32);
        _docTableOff = _view.ReadInt64(40);

        // Reject a structurally-corrupt/tampered file at open (offsets must be ascending and in
        // bounds) so callers rebuild instead of hitting an OOB/huge-alloc on a later read.
        long cap = _view.Capacity;
        if (DocCount < 0 || TermCount < 0 || _termKeysOff < 0 || _termInfoOff < _termKeysOff ||
            _postingsOff < _termInfoOff || _docTableOff < _postingsOff || _docTableOff > cap)
            throw new InvalidDataException("corrupt CodeCompass segment (bad section offsets)");
        // Each fixed-width region must be large enough for its declared count, else a later read of a term/doc
        // index would run into the next section or off the end. (long math: counts can't overflow the offsets.)
        if (_termInfoOff - _termKeysOff < (long)TermCount * 8 ||     // term keys: 8 bytes each
            _postingsOff - _termInfoOff < (long)TermCount * 12 ||    // term info: (rel:8, len:4) each
            cap - _docTableOff < (long)(DocCount + 1) * 4)           // doc offset table: (DocCount+1) ints
            throw new InvalidDataException("corrupt CodeCompass segment (section too small for its counts)");
    }

    /// <summary>The i-th trigram key (keys are stored sorted). For enumerating a segment during a merge.</summary>
    public long GetTermKey(int i)
    {
        if ((uint)i >= (uint)TermCount) throw new InvalidDataException("segment term index out of range");
        return _view.ReadInt64(_termKeysOff + (long)i * 8);
    }

    /// <summary>Postings (local docIds) for the i-th term, by index (no binary search).</summary>
    public int[] GetPostingsAt(int i)
    {
        if ((uint)i >= (uint)TermCount) throw new InvalidDataException("segment term index out of range");
        long rel = _view.ReadInt64(_termInfoOff + (long)i * 12);
        int len = _view.ReadInt32(_termInfoOff + (long)i * 12 + 8);
        return DecodePostings(_postingsOff + rel, len);
    }

    /// <summary>Local docIds containing the trigram, or null if the segment has no such term.</summary>
    public int[]? GetPostings(long key)
    {
        int lo = 0, hi = TermCount - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            long k = _view.ReadInt64(_termKeysOff + (long)mid * 8);
            if (k == key)
            {
                long rel = _view.ReadInt64(_termInfoOff + (long)mid * 12);
                int len = _view.ReadInt32(_termInfoOff + (long)mid * 12 + 8);
                return DecodePostings(_postingsOff + rel, len);
            }
            if (k < key) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }

    private int[] DecodePostings(long offset, int len)
    {
        // Trust nothing from the file: the postings blob must lie wholly within the postings region
        // [_postingsOff, _docTableOff). A corrupt (rel,len) would otherwise huge-alloc, over-read, or
        // (negative len) throw an opaque OverflowException; convert it to a clean corruption signal.
        if (len < 0 || offset < _postingsOff || offset + len > _docTableOff)
            throw new InvalidDataException("corrupt CodeCompass segment (postings out of range)");
        var bytes = new byte[len];
        _view.ReadArray(offset, bytes, 0, len);
        var span = (ReadOnlySpan<byte>)bytes;

        // Count varints first, then fill an exact-size array. Avoids a growing List<int> (repeated
        // reallocation as a long posting list grows) plus its ToArray() copy - two allocations become one,
        // with no copy. This runs once per query-trigram-group per segment, so it is squarely on the query
        // hot path; the two passes are over a small in-memory buffer (cache-resident), not the mmap.
        int count = 0, pos = 0;
        while (pos < len) { Varint.Read(span, ref pos); count++; }

        var result = new int[count];
        pos = 0;
        int prev = 0;
        for (int i = 0; i < count; i++) { prev += (int)Varint.Read(span, ref pos); result[i] = prev; }
        return result;
    }

    public string GetPath(int localDocId)
    {
        if ((uint)localDocId >= (uint)DocCount) throw new InvalidDataException("segment doc id out of range");
        int o0 = _view.ReadInt32(_docTableOff + (long)localDocId * 4);
        int o1 = _view.ReadInt32(_docTableOff + (long)(localDocId + 1) * 4);
        long blobStart = _docTableOff + (long)(DocCount + 1) * 4;
        // Guard corrupt offsets: monotonic (0 <= o0 <= o1) and the slice within the mapped file, so a bad
        // entry can't produce a negative length (throwing OverflowException) or an out-of-bounds read.
        if (o0 < 0 || o1 < o0 || blobStart + o1 > _view.Capacity)
            throw new InvalidDataException("corrupt CodeCompass segment (path offsets out of range)");
        int len = o1 - o0;
        if (len == 0) return "";
        var buf = new byte[len];
        _view.ReadArray(blobStart + o0, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
