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
        if (_view.ReadInt32(4) != SegmentBuilder.Version) throw new InvalidDataException("unsupported segment version");
        DocCount = _view.ReadInt32(8);
        TermCount = _view.ReadInt32(12);
        _termKeysOff = _view.ReadInt64(16);
        _termInfoOff = _view.ReadInt64(24);
        _postingsOff = _view.ReadInt64(32);
        _docTableOff = _view.ReadInt64(40);
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
        var bytes = new byte[len];
        _view.ReadArray(offset, bytes, 0, len);

        var result = new List<int>();
        int pos = 0, prev = 0;
        var span = (ReadOnlySpan<byte>)bytes;
        while (pos < len)
        {
            prev += (int)Varint.Read(span, ref pos);
            result.Add(prev);
        }
        return result.ToArray();
    }

    public string GetPath(int localDocId)
    {
        int o0 = _view.ReadInt32(_docTableOff + (long)localDocId * 4);
        int o1 = _view.ReadInt32(_docTableOff + (long)(localDocId + 1) * 4);
        long blobStart = _docTableOff + (long)(DocCount + 1) * 4;
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
