using System.IO.MemoryMappedFiles;
using System.Text;

namespace CodeCompass.Core.Changes.Segments;

/// <summary>
/// An immutable, path-sorted, memory-mapped snapshot base file. Change detection reads it
/// via mmap - a lookup is a binary search over the sorted paths, and a full pass streams
/// paths on demand - so the change ledger for a huge repo never has to sit in RAM.
///
/// Columnar layout (little-endian): header, then path offsets, the UTF-8 path blob (sorted),
/// sizes (int64), mtimes (int64), and raw 32-byte SHA-256 hashes. Fixed-width columns make
/// every field directly indexable by record number.
/// </summary>
public static class SnapshotBaseFile
{
    internal const uint Magic = 0x4E535343; // "CCSN"
    internal const int Version = 1;
    internal const int HeaderSize = 12 + 8 * 5;
    internal const int HashBytes = 32; // SHA-256

    /// <summary>
    /// Write a base file from a sorted entry sequence. The caller supplies the record count
    /// and total UTF-8 path-blob length so the whole file can be laid out and filled in a
    /// single streaming pass (no large in-RAM buffers). Entries MUST be sorted by path using
    /// ordinal comparison and MUST match <paramref name="count"/> / <paramref name="pathBlobLen"/>.
    /// </summary>
    public static void Write(string filePath, int count, long pathBlobLen,
                             IEnumerable<(string Path, FileState State)> sortedEntries)
    {
        long pathOffsetsOff = HeaderSize;
        long pathBlobOff = pathOffsetsOff + (long)(count + 1) * 4;
        long sizesOff = pathBlobOff + pathBlobLen;
        long mtimesOff = sizesOff + (long)count * 8;
        long hashesOff = mtimesOff + (long)count * 8;
        long end = hashesOff + (long)count * HashBytes;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fs.SetLength(end);
        using var mmf = MemoryMappedFile.CreateFromFile(fs, mapName: null, end,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        using var view = mmf.CreateViewAccessor(0, end, MemoryMappedFileAccess.ReadWrite);

        view.Write(0, Magic);
        view.Write(4, Version);
        view.Write(8, count);
        view.Write(12, pathOffsetsOff);
        view.Write(20, pathBlobOff);
        view.Write(28, sizesOff);
        view.Write(36, mtimesOff);
        view.Write(44, hashesOff);

        int i = 0;
        int pathAcc = 0;
        var hashBuf = new byte[HashBytes];
        foreach (var (path, state) in sortedEntries)
        {
            if (i >= count) throw new InvalidOperationException("snapshot base: more entries than declared count");
            view.Write(pathOffsetsOff + (long)i * 4, pathAcc);

            var pb = Encoding.UTF8.GetBytes(path);
            if (pb.Length > 0) view.WriteArray(pathBlobOff + pathAcc, pb, 0, pb.Length);
            pathAcc += pb.Length;

            view.Write(sizesOff + (long)i * 8, state.Size);
            view.Write(mtimesOff + (long)i * 8, state.MTimeTicks);

            FillHash(hashBuf, state.ContentHash);
            view.WriteArray(hashesOff + (long)i * HashBytes, hashBuf, 0, HashBytes);
            i++;
        }
        if (i != count) throw new InvalidOperationException($"snapshot base: wrote {i} entries, declared {count}");
        view.Write(pathOffsetsOff + (long)count * 4, pathAcc); // terminating offset
        if (pathAcc != pathBlobLen) throw new InvalidOperationException(
            $"snapshot base: path blob was {pathAcc} bytes, declared {pathBlobLen}");
    }

    private static void FillHash(byte[] buf, string hexHash)
    {
        Array.Clear(buf);
        if (hexHash.Length != HashBytes * 2) return; // defensive: unexpected -> zero hash (file re-detected as changed)
        try { Convert.FromHexString(hexHash).AsSpan(0, HashBytes).CopyTo(buf); }
        catch { Array.Clear(buf); }
    }
}

/// <summary>Reads a <see cref="SnapshotBaseFile"/> via mmap. Never loads the file into RAM.</summary>
public sealed class SnapshotBaseReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _pathOffsetsOff, _pathBlobOff, _sizesOff, _mtimesOff, _hashesOff;

    public int Count { get; }
    public string FilePath { get; }

    /// <summary>Total UTF-8 bytes of all paths (used to size a rewrite without a scan).</summary>
    public long PathBlobLen => _sizesOff - _pathBlobOff;

    public SnapshotBaseReader(string filePath)
    {
        FilePath = filePath;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        if (_view.ReadUInt32(0) != SnapshotBaseFile.Magic) throw new InvalidDataException("not a CodeCompass snapshot base");
        if (_view.ReadInt32(4) != SnapshotBaseFile.Version) throw new InvalidDataException("unsupported snapshot base version");
        Count = _view.ReadInt32(8);
        _pathOffsetsOff = _view.ReadInt64(12);
        _pathBlobOff = _view.ReadInt64(20);
        _sizesOff = _view.ReadInt64(28);
        _mtimesOff = _view.ReadInt64(36);
        _hashesOff = _view.ReadInt64(44);
    }

    public string GetPath(int i)
    {
        int o0 = _view.ReadInt32(_pathOffsetsOff + (long)i * 4);
        int o1 = _view.ReadInt32(_pathOffsetsOff + (long)(i + 1) * 4);
        int len = o1 - o0;
        if (len == 0) return "";
        var buf = new byte[len];
        _view.ReadArray(_pathBlobOff + o0, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    public FileState GetState(int i)
    {
        long size = _view.ReadInt64(_sizesOff + (long)i * 8);
        long mtime = _view.ReadInt64(_mtimesOff + (long)i * 8);
        var hb = new byte[SnapshotBaseFile.HashBytes];
        _view.ReadArray(_hashesOff + (long)i * SnapshotBaseFile.HashBytes, hb, 0, hb.Length);
        return new FileState(size, mtime, Convert.ToHexString(hb));
    }

    /// <summary>Binary search for an exact path.</summary>
    public bool TryFind(string path, out FileState state)
    {
        int idx = IndexOf(path);
        if (idx < 0) { state = default; return false; }
        state = GetState(idx);
        return true;
    }

    public bool Contains(string path) => IndexOf(path) >= 0;

    private int IndexOf(string path)
    {
        int lo = 0, hi = Count - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int cmp = string.CompareOrdinal(GetPath(mid), path);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Index of the first path &gt;= <paramref name="key"/> (for prefix range scans).</summary>
    public int LowerBound(string key)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (string.CompareOrdinal(GetPath(mid), key) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>All (path, state) in sorted order - streamed, one string at a time.</summary>
    public IEnumerable<(string Path, FileState State)> Enumerate()
    {
        for (int i = 0; i < Count; i++) yield return (GetPath(i), GetState(i));
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
