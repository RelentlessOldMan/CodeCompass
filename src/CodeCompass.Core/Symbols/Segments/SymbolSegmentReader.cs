using CodeCompass.Core.Storage;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace CodeCompass.Core.Symbols.Segments;

/// <summary>
/// Reads an immutable symbol segment via a memory-mapped file. Names are read on demand;
/// find-by-name is a binary search over the sorted names and search-by-substring is a
/// sequential name scan - neither loads the segment into RAM.
/// </summary>
public sealed class SymbolSegmentReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _nameOffsetsOff, _kindsOff, _pathIdsOff, _linesOff, _endLinesOff, _colsOff, _nameBlobOff, _pathOffsetsOff, _pathBlobOff;
    private readonly long _cap;

    public int Count { get; }
    public int PathCount { get; }
    public string FilePath { get; }

    public SymbolSegmentReader(string filePath)
    {
        FilePath = filePath;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        if (_view.ReadUInt32(0) != SymbolSegmentBuilder.Magic) throw new InvalidDataException("not a CodeCompass symbol segment");
        int version = _view.ReadInt32(4);
        if (version > SymbolSegmentBuilder.Version) throw new IndexFormatTooNewException("symbol segment", version, SymbolSegmentBuilder.Version);
        if (version != SymbolSegmentBuilder.Version) throw new InvalidDataException("unsupported symbol segment version");
        Count = _view.ReadInt32(8);
        PathCount = _view.ReadInt32(12);
        _nameOffsetsOff = _view.ReadInt64(16);
        _kindsOff = _view.ReadInt64(24);
        _pathIdsOff = _view.ReadInt64(32);
        _linesOff = _view.ReadInt64(40);
        _endLinesOff = _view.ReadInt64(48);
        _colsOff = _view.ReadInt64(56);
        _nameBlobOff = _view.ReadInt64(64);
        _pathOffsetsOff = _view.ReadInt64(72);
        _pathBlobOff = _view.ReadInt64(80);

        // Reject a structurally-corrupt/tampered segment at open so callers rebuild.
        long cap = _view.Capacity;
        _cap = cap;
        if (Count < 0 || PathCount < 0 || _nameOffsetsOff < 0 || _kindsOff < _nameOffsetsOff ||
            _pathIdsOff < _kindsOff || _linesOff < _pathIdsOff || _endLinesOff < _linesOff ||
            _colsOff < _endLinesOff || _nameBlobOff < _colsOff || _pathOffsetsOff < _nameBlobOff ||
            _pathBlobOff < _pathOffsetsOff || _pathBlobOff > cap)
            throw new InvalidDataException("corrupt CodeCompass symbol segment (bad section offsets)");
        // Each fixed-width column must be large enough for its declared count, else a later read of a
        // symbol/path index would run past its section or off the mapped view. (Mirrors SegmentReader's
        // count-size check; long math so counts can't overflow the offset arithmetic.)
        if (_kindsOff - _nameOffsetsOff < (long)(Count + 1) * 4 || // name offsets: (Count+1) ints
            _pathIdsOff - _kindsOff < (long)Count ||               // kinds: 1 byte each
            _linesOff - _pathIdsOff < (long)Count * 4 ||           // pathIds
            _endLinesOff - _linesOff < (long)Count * 4 ||          // lines
            _colsOff - _endLinesOff < (long)Count * 4 ||           // endLines
            _nameBlobOff - _colsOff < (long)Count * 4 ||           // cols
            _pathBlobOff - _pathOffsetsOff < (long)(PathCount + 1) * 4) // path offsets: (PathCount+1) ints
            throw new InvalidDataException("corrupt CodeCompass symbol segment (section too small for its counts)");
    }

    public string GetName(int i)
    {
        if ((uint)i >= (uint)Count) throw new InvalidDataException("symbol segment name index out of range");
        int o0 = _view.ReadInt32(_nameOffsetsOff + (long)i * 4);
        int o1 = _view.ReadInt32(_nameOffsetsOff + (long)(i + 1) * 4);
        // Guard corrupt offsets: monotonic (0 <= o0 <= o1) and within the name blob, so a bad entry can't
        // produce a negative length (OverflowException on new byte[len]) or an out-of-bounds read.
        if (o0 < 0 || o1 < o0 || _nameBlobOff + o1 > _pathOffsetsOff)
            throw new InvalidDataException("corrupt CodeCompass symbol segment (name offsets out of range)");
        return ReadUtf8(_nameBlobOff + o0, o1 - o0);
    }

    public Symbol GetSymbol(int i)
    {
        if ((uint)i >= (uint)Count) throw new InvalidDataException("symbol segment index out of range");
        var kind = (SymbolKind)_view.ReadByte(_kindsOff + i);
        int pid = _view.ReadInt32(_pathIdsOff + (long)i * 4);
        int line = _view.ReadInt32(_linesOff + (long)i * 4);
        int endLine = _view.ReadInt32(_endLinesOff + (long)i * 4);
        int col = _view.ReadInt32(_colsOff + (long)i * 4);
        return new Symbol(GetName(i), kind, ReadPath(pid), line, col) { EndLine = endLine };
    }

    public string GetSymbolPath(int i)
    {
        if ((uint)i >= (uint)Count) throw new InvalidDataException("symbol segment index out of range");
        return ReadPath(_view.ReadInt32(_pathIdsOff + (long)i * 4));
    }

    /// <summary>Local indices of symbols whose name equals <paramref name="name"/> (binary search).</summary>
    public IEnumerable<int> FindByName(string name)
    {
        int lo = 0, hi = Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int cmp = string.CompareOrdinal(GetName(mid), name);
            if (cmp == 0) { found = mid; break; }
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        if (found < 0) yield break;

        int start = found;
        while (start > 0 && string.CompareOrdinal(GetName(start - 1), name) == 0) start--;
        int end = found;
        while (end < Count - 1 && string.CompareOrdinal(GetName(end + 1), name) == 0) end++;
        for (int i = start; i <= end; i++) yield return i;
    }

    public bool ContainsPath(string path)
    {
        int lo = 0, hi = PathCount - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int cmp = string.CompareOrdinal(ReadPath(mid), path);
            if (cmp == 0) return true;
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        return false;
    }

    private string ReadPath(int pathId)
    {
        if ((uint)pathId >= (uint)PathCount) throw new InvalidDataException("symbol segment path id out of range");
        int o0 = _view.ReadInt32(_pathOffsetsOff + (long)pathId * 4);
        int o1 = _view.ReadInt32(_pathOffsetsOff + (long)(pathId + 1) * 4);
        // Guard corrupt offsets: monotonic and within the path blob (which runs to the end of the view).
        if (o0 < 0 || o1 < o0 || _pathBlobOff + o1 > _cap)
            throw new InvalidDataException("corrupt CodeCompass symbol segment (path offsets out of range)");
        return ReadUtf8(_pathBlobOff + o0, o1 - o0);
    }

    private string ReadUtf8(long offset, int len)
    {
        if (len == 0) return "";
        var buf = new byte[len];
        _view.ReadArray(offset, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
