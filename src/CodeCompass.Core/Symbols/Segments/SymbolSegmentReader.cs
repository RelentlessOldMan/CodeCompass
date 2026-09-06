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
    private readonly long _nameOffsetsOff, _kindsOff, _pathIdsOff, _linesOff, _colsOff, _nameBlobOff, _pathOffsetsOff, _pathBlobOff;

    public int Count { get; }
    public int PathCount { get; }
    public string FilePath { get; }

    public SymbolSegmentReader(string filePath)
    {
        FilePath = filePath;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        if (_view.ReadUInt32(0) != SymbolSegmentBuilder.Magic) throw new InvalidDataException("not a CodeCompass symbol segment");
        if (_view.ReadInt32(4) != SymbolSegmentBuilder.Version) throw new InvalidDataException("unsupported symbol segment version");
        Count = _view.ReadInt32(8);
        PathCount = _view.ReadInt32(12);
        _nameOffsetsOff = _view.ReadInt64(16);
        _kindsOff = _view.ReadInt64(24);
        _pathIdsOff = _view.ReadInt64(32);
        _linesOff = _view.ReadInt64(40);
        _colsOff = _view.ReadInt64(48);
        _nameBlobOff = _view.ReadInt64(56);
        _pathOffsetsOff = _view.ReadInt64(64);
        _pathBlobOff = _view.ReadInt64(72);
    }

    public string GetName(int i)
    {
        int o0 = _view.ReadInt32(_nameOffsetsOff + (long)i * 4);
        int o1 = _view.ReadInt32(_nameOffsetsOff + (long)(i + 1) * 4);
        return ReadUtf8(_nameBlobOff + o0, o1 - o0);
    }

    public Symbol GetSymbol(int i)
    {
        var kind = (SymbolKind)_view.ReadByte(_kindsOff + i);
        int pid = _view.ReadInt32(_pathIdsOff + (long)i * 4);
        int line = _view.ReadInt32(_linesOff + (long)i * 4);
        int col = _view.ReadInt32(_colsOff + (long)i * 4);
        return new Symbol(GetName(i), kind, ReadPath(pid), line, col);
    }

    public string GetSymbolPath(int i) => ReadPath(_view.ReadInt32(_pathIdsOff + (long)i * 4));

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
        int o0 = _view.ReadInt32(_pathOffsetsOff + (long)pathId * 4);
        int o1 = _view.ReadInt32(_pathOffsetsOff + (long)(pathId + 1) * 4);
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
