using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeCompass.Core.Indexing.Segments;

/// <summary>
/// Merges segments into one, dropping tombstoned docs, by walking every segment's sorted term list together (a k-way
/// merge) and writing each term's remapped postings straight to the file. Compaction used to rebuild each live doc's
/// trigram list in RAM (a List per doc, a whole segment's worth at once) and re-add it to a builder - ~300 MB at peak in
/// the long-lived session. This holds a doc-id remap (an int a doc) and one term's postings at a time.
///
/// Same layout as <see cref="SegmentBuilder"/>: live docs keep their order (segment by segment), so a merge of
/// tombstone-free segments is byte-identical to building them in one go. A term whose every posting was tombstoned is
/// dropped, and the key and info regions were sized for every term before the merge knew it, so they end in unused
/// slack (20 bytes a dropped term) - the reader allows a region larger than its count.
/// </summary>
internal static class SegmentMerger
{
    /// <summary>Writes the merged segment to <paramref name="filePath"/> and returns its doc count; 0 (no live doc)
    /// writes nothing.</summary>
    public static int Merge(IReadOnlyList<SegmentReader> segs, IReadOnlyDictionary<int, HashSet<int>> tombstones, string filePath)
    {
        // New ids: the live docs in order.
        var remap = new int[segs.Count][];
        int live = 0;
        for (int s = 0; s < segs.Count; s++)
        {
            tombstones.TryGetValue(s, out var tomb);
            var m = remap[s] = new int[segs[s].DocCount];
            for (int d = 0; d < m.Length; d++) m[d] = tomb is not null && tomb.Contains(d) ? -1 : live++;
        }
        if (live == 0) return 0;

        // Pass 1: how many distinct terms (an upper bound on what's kept), to place the regions.
        long distinct = 0;
        ForEachTerm(segs, (_, _) => distinct++);
        if (distinct > int.MaxValue) throw new InvalidOperationException("too many distinct trigrams to merge into one segment");

        long termKeysOff = SegmentBuilder.HeaderSize;
        long termInfoOff = termKeysOff + distinct * 8;
        long postingsOff = termInfoOff + distinct * 12;

        using var handle = File.OpenHandle(filePath, FileMode.Create, FileAccess.Write);
        var keys = new RegionWriter(handle, termKeysOff);
        var info = new RegionWriter(handle, termInfoOff);
        var postings = new RegionWriter(handle, postingsOff);
        int terms = 0;
        ForEachTerm(segs, (key, members) =>
        {
            long rel = postings.Written;
            int prev = 0;
            foreach (var (s, t) in members) // ascending segment -> ascending new ids
            {
                var m = remap[s];
                foreach (var d in segs[s].GetPostingsAt(t))
                {
                    int nd = m[d];
                    if (nd < 0) continue;
                    postings.WriteVarint((uint)(nd - prev));
                    prev = nd;
                }
            }
            long len = postings.Written - rel;
            if (len == 0) return; // every doc with this term was deleted
            keys.WriteInt64(key);
            info.WriteInt64(rel);
            info.WriteInt32(checked((int)len));
            terms++;
        });
        keys.Flush(); info.Flush(); postings.Flush();

        // Doc table: (live+1) path offsets, then the path blob.
        long docTableOff = postingsOff + postings.Written;
        var offsets = new RegionWriter(handle, docTableOff);
        var blob = new RegionWriter(handle, docTableOff + (long)(live + 1) * 4);
        int acc = 0;
        for (int s = 0; s < segs.Count; s++)
            for (int d = 0; d < remap[s].Length; d++)
            {
                if (remap[s][d] < 0) continue;
                var bytes = Encoding.UTF8.GetBytes(segs[s].GetPath(d));
                offsets.WriteInt32(acc);
                blob.Write(bytes);
                acc = checked(acc + bytes.Length);
            }
        offsets.WriteInt32(acc); // final sentinel offset
        offsets.Flush(); blob.Flush();

        var header = new RegionWriter(handle, 0);
        header.WriteInt32(unchecked((int)SegmentBuilder.Magic));
        header.WriteInt32(SegmentBuilder.Version);
        header.WriteInt32(live);
        header.WriteInt32(terms);
        header.WriteInt64(termKeysOff);
        header.WriteInt64(termInfoOff);
        header.WriteInt64(postingsOff);
        header.WriteInt64(docTableOff);
        header.Flush();
        // Durable before any manifest names it (see SegmentBuilder.WriteTo).
        RandomAccess.FlushToDisk(handle);
        return live;
    }

    /// <summary>Each distinct key in ascending order, with the (segment, term index) pairs holding it, segments ascending.</summary>
    private static void ForEachTerm(IReadOnlyList<SegmentReader> segs, Action<long, List<(int Seg, int Term)>> onTerm)
    {
        var heads = new PriorityQueue<(int Seg, int Term), (long Key, int Seg)>();
        for (int s = 0; s < segs.Count; s++)
            if (segs[s].TermCount > 0) heads.Enqueue((s, 0), (segs[s].GetTermKey(0), s));
        var members = new List<(int Seg, int Term)>();
        while (heads.TryDequeue(out var head, out var pri))
        {
            members.Clear();
            members.Add(head);
            while (heads.TryPeek(out _, out var next) && next.Key == pri.Key) members.Add(heads.Dequeue());
            onTerm(pri.Key, members);
            foreach (var (s, t) in members)
                if (t + 1 < segs[s].TermCount) heads.Enqueue((s, t + 1), (segs[s].GetTermKey(t + 1), s));
        }
    }

    /// <summary>A buffered little-endian writer for one region of the file, from its start offset on.</summary>
    private sealed class RegionWriter(SafeFileHandle handle, long start)
    {
        private readonly byte[] _buf = new byte[1 << 16];
        private int _used;
        private long _flushed;

        public long Written => _flushed + _used;

        public void WriteVarint(uint value)
        {
            if (_used + 5 > _buf.Length) Flush();
            while (value >= 0x80) { _buf[_used++] = (byte)(value | 0x80); value >>= 7; }
            _buf[_used++] = (byte)value;
        }

        public void WriteInt32(int v)
        {
            if (_used + 4 > _buf.Length) Flush();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_used), v);
            _used += 4;
        }

        public void WriteInt64(long v)
        {
            if (_used + 8 > _buf.Length) Flush();
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_used), v);
            _used += 8;
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            if (_used + bytes.Length > _buf.Length) Flush();
            if (bytes.Length > _buf.Length) { RandomAccess.Write(handle, bytes, start + _flushed); _flushed += bytes.Length; return; }
            bytes.CopyTo(_buf.AsSpan(_used));
            _used += bytes.Length;
        }

        public void Flush()
        {
            if (_used == 0) return;
            RandomAccess.Write(handle, _buf.AsSpan(0, _used), start + _flushed);
            _flushed += _used;
            _used = 0;
        }
    }
}
