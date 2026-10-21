using CodeCompass.Core.Storage;
using System.Buffers;
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
        // A file that fails the checks below (not a segment, a newer format, corrupt) must not keep its mapping open until
        // a GC: every retry of a failed load leaked one, and the open mapping blocks overwriting or deleting the file.
        try
        {
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
        catch
        {
            _view?.Dispose();
            _mmf.Dispose();
            throw;
        }
    }

    public string GetName(int i)
    {
        long off = NameSpan(i, out int len);
        return ReadUtf8(off, len);
    }

    /// <summary>Does symbol i's name contain <paramref name="value"/> (OrdinalIgnoreCase)? The substring scan asks this
    /// of every symbol, so it reads the name in place instead of building it.</summary>
    public bool NameContains(int i, string value)
    {
        long off = NameSpan(i, out int len);
        return MappedUtf8.Contains(_view, off, len, value, StringComparison.OrdinalIgnoreCase);
    }

    // Names read per scan step: bounded RAM however big the segment (a longer single name gets a bigger buffer).
    internal const int ScanChunkBytes = 64 * 1024;
    private const int ScanChunkNames = 4096;

    /// <summary>Local indices, in order, of the symbols whose name contains <paramref name="value"/> (OrdinalIgnoreCase):
    /// the same answer as <see cref="NameContains"/> on each, but an ASCII query scans the name bytes a chunk at a time
    /// instead of decoding every name - search_symbols' miss path, which visits every symbol.</summary>
    public IEnumerable<int> NamesContaining(string value)
    {
        if (value.Length == 0 || !Ascii.IsValid(value))
        {
            for (int i = 0; i < Count; i++) if (NameContains(i, value)) yield return i;
            yield break;
        }
        var query = Encoding.ASCII.GetBytes(value);
        var hits = new List<int>();
        byte[] blob = ArrayPool<byte>.Shared.Rent(ScanChunkBytes);
        int[] offs = ArrayPool<int>.Shared.Rent(ScanChunkNames + 1);
        try
        {
            for (int first = 0; first < Count;)
            {
                hits.Clear();
                first = ScanChunk(first, query, ref blob, offs, hits);
                foreach (var i in hits) yield return i;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blob);
            ArrayPool<int>.Shared.Return(offs);
        }
    }

    // One chunk of names from `first`: appends the matches (in order) to `hits`, returns the next unscanned name.
    private int ScanChunk(int first, byte[] query, ref byte[] blob, int[] offs, List<int> hits)
    {
        int n = Math.Min(ScanChunkNames, Count - first);
        var o = offs.AsSpan(0, n + 1);
        ReadMapped(_nameOffsetsOff + (long)first * 4, o);
        for (int k = 0; k <= n; k++) // the same guard NameSpan applies to each name it reads
            if (o[k] < 0 || (k > 0 && o[k] < o[k - 1]) || _nameBlobOff + o[k] > _pathOffsetsOff)
                throw new InvalidDataException("corrupt CodeCompass symbol segment (name offsets out of range)");
        int count = 1; // always at least one name, however long
        while (count < n && o[count + 1] - o[0] <= ScanChunkBytes) count++;
        int total = o[count] - o[0];
        if (total > blob.Length) { ArrayPool<byte>.Shared.Return(blob); blob = ArrayPool<byte>.Shared.Rent(total); }
        var bytes = blob.AsSpan(0, total);
        ReadMapped(_nameBlobOff + o[0], bytes);

        // Exact for an ASCII query: ASCII bytes are the same chars in UTF-8 (decoding never swallows one), and no non-ASCII
        // char equals an ASCII one under OrdinalIgnoreCase (SymbolNameScanTests pins that), so the decoded name matches
        // exactly when its bytes do. A match that runs on into the next name is not one.
        int j = 0;
        for (int pos = 0; ;)
        {
            int p = IndexOfAsciiIgnoreCase(bytes, pos, query);
            if (p < 0) break;
            while (o[j + 1] - o[0] <= p) j++;
            if (p + query.Length <= o[j + 1] - o[0]) { hits.Add(first + j); pos = o[j + 1] - o[0]; j++; if (j == count) break; }
            else pos = p + 1;
        }
        return first + count;
    }

    // First index at or after `from` where `query` (ASCII) occurs in `bytes`, ignoring ASCII case; -1 if none.
    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> bytes, int from, byte[] query)
    {
        byte c = query[0];
        bool letter = (uint)((c | 0x20) - 'a') <= 'z' - 'a';
        byte lo = (byte)(c | 0x20), up = (byte)(c & ~0x20);
        while (from <= bytes.Length - query.Length)
        {
            var rest = bytes[from..];
            int k = letter ? rest.IndexOfAny(lo, up) : rest.IndexOf(c);
            if (k < 0) return -1;
            int p = from + k;
            if (p > bytes.Length - query.Length) return -1;
            if (Ascii.EqualsIgnoreCase(bytes.Slice(p, query.Length), query)) return p;
            from = p + 1;
        }
        return -1;
    }

    private void ReadMapped<T>(long offset, Span<T> into) where T : struct =>
        _view.SafeMemoryMappedViewHandle.ReadSpan((ulong)(_view.PointerOffset + offset), into);

    private int CompareName(int i, string name)
    {
        long off = NameSpan(i, out int len);
        return MappedUtf8.CompareOrdinal(_view, off, len, name);
    }

    // Where name i's UTF-8 bytes are. Guard corrupt offsets: monotonic (0 <= o0 <= o1) and within the name blob, so a bad
    // entry can't produce a negative length or an out-of-bounds read.
    private long NameSpan(int i, out int len)
    {
        if ((uint)i >= (uint)Count) throw new InvalidDataException("symbol segment name index out of range");
        int o0 = _view.ReadInt32(_nameOffsetsOff + (long)i * 4);
        int o1 = _view.ReadInt32(_nameOffsetsOff + (long)(i + 1) * 4);
        if (o0 < 0 || o1 < o0 || _nameBlobOff + o1 > _pathOffsetsOff)
            throw new InvalidDataException("corrupt CodeCompass symbol segment (name offsets out of range)");
        len = o1 - o0;
        return _nameBlobOff + o0;
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
            int cmp = CompareName(mid, name);
            if (cmp == 0) { found = mid; break; }
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        if (found < 0) yield break;

        int start = found;
        while (start > 0 && CompareName(start - 1, name) == 0) start--;
        int end = found;
        while (end < Count - 1 && CompareName(end + 1, name) == 0) end++;
        for (int i = start; i <= end; i++) yield return i;
    }

    public bool ContainsPath(string path)
    {
        int lo = 0, hi = PathCount - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            long off = PathSpan(mid, out int len);
            int cmp = MappedUtf8.CompareOrdinal(_view, off, len, path);
            if (cmp == 0) return true;
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        return false;
    }

    /// <summary>Path-table entry <paramref name="pathId"/> (0..PathCount-1): each distinct path with a symbol, once.</summary>
    public string GetPath(int pathId) => ReadPath(pathId);

    private string ReadPath(int pathId)
    {
        long off = PathSpan(pathId, out int len);
        return ReadUtf8(off, len);
    }

    // Guard corrupt offsets: monotonic and within the path blob (which runs to the end of the view).
    private long PathSpan(int pathId, out int len)
    {
        if ((uint)pathId >= (uint)PathCount) throw new InvalidDataException("symbol segment path id out of range");
        int o0 = _view.ReadInt32(_pathOffsetsOff + (long)pathId * 4);
        int o1 = _view.ReadInt32(_pathOffsetsOff + (long)(pathId + 1) * 4);
        if (o0 < 0 || o1 < o0 || _pathBlobOff + o1 > _cap)
            throw new InvalidDataException("corrupt CodeCompass symbol segment (path offsets out of range)");
        len = o1 - o0;
        return _pathBlobOff + o0;
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
