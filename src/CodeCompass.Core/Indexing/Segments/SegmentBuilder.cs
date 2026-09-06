using System.Text;

namespace CodeCompass.Core.Indexing.Segments;

/// <summary>
/// Accumulates documents (path + distinct trigrams) in memory and writes them as one
/// immutable on-disk segment. A build flushes a segment whenever <see cref="ApproxBytes"/>
/// crosses a budget, so build memory stays bounded no matter how large the repo is.
///
/// Segment file layout (little-endian):
///   header: magic(4) version(4) docCount(4) termCount(4)
///           termKeysOff(8) termInfoOff(8) postingsOff(8) docTableOff(8)
///   term keys:  termCount * int64, ascending (binary-searchable)
///   term info:  termCount * (int64 postingRelOffset, int32 byteLen)
///   postings:   per term, delta-varint local docIds
///   doc table:  (docCount+1) * int32 path offsets, then UTF-8 path blob
/// </summary>
public sealed class SegmentBuilder
{
    internal const uint Magic = 0x47534343; // "CCSG"
    internal const int Version = 1;
    internal const int HeaderSize = 4 + 4 + 4 + 4 + 8 * 4;

    private readonly List<string> _paths = new();
    private readonly Dictionary<long, List<int>> _postings = new();

    public int DocCount => _paths.Count;
    public long ApproxBytes { get; private set; }

    public void AddDocument(string relPath, long[] distinctTrigrams)
    {
        int id = _paths.Count;
        _paths.Add(relPath);
        foreach (var t in distinctTrigrams)
        {
            if (!_postings.TryGetValue(t, out var list))
            {
                list = new List<int>();
                _postings[t] = list;
            }
            list.Add(id); // ids added in increasing order -> lists stay sorted
        }
        ApproxBytes += (long)distinctTrigrams.Length * 5 + relPath.Length * 2 + 24;
    }

    public void WriteTo(string filePath)
    {
        var keys = _postings.Keys.ToArray();
        Array.Sort(keys);

        // Postings blob + per-term (offset,len).
        var postings = new MemoryStream();
        var termRelOffsets = new long[keys.Length];
        var termLens = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            termRelOffsets[i] = postings.Position;
            int prev = 0;
            foreach (var d in _postings[keys[i]])
            {
                Varint.Write(postings, (uint)(d - prev));
                prev = d;
            }
            termLens[i] = (int)(postings.Position - termRelOffsets[i]);
        }

        // Doc table: path offsets + blob.
        var pathBytes = new byte[_paths.Count][];
        int blobLen = 0;
        for (int i = 0; i < _paths.Count; i++)
        {
            pathBytes[i] = Encoding.UTF8.GetBytes(_paths[i]);
            blobLen += pathBytes[i].Length;
        }

        long termKeysOff = HeaderSize;
        long termInfoOff = termKeysOff + (long)keys.Length * 8;
        long postingsOff = termInfoOff + (long)keys.Length * 12;
        long docTableOff = postingsOff + postings.Length;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        w.Write(Magic);
        w.Write(Version);
        w.Write(_paths.Count);
        w.Write(keys.Length);
        w.Write(termKeysOff);
        w.Write(termInfoOff);
        w.Write(postingsOff);
        w.Write(docTableOff);

        foreach (var k in keys) w.Write(k);
        for (int i = 0; i < keys.Length; i++) { w.Write(termRelOffsets[i]); w.Write(termLens[i]); }
        w.Flush();
        postings.Position = 0;
        postings.CopyTo(fs);

        int acc = 0;
        for (int i = 0; i < pathBytes.Length; i++) { w.Write(acc); acc += pathBytes[i].Length; }
        w.Write(acc); // final sentinel offset
        foreach (var b in pathBytes) w.Write(b);
        w.Flush();
    }
}
