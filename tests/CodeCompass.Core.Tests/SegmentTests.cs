using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

public class SegmentTests
{
    private static long Trigram(string s) => TrigramIndex.ComputeTrigrams(s)[0];

    [Fact]
    public void WriteThenRead_RoundTripsPostingsAndPaths()
    {
        var b = new SegmentBuilder();
        b.AddDocument("a.cs", TrigramIndex.ComputeTrigrams("hello world"));
        b.AddDocument("dir/b.cs", TrigramIndex.ComputeTrigrams("hello there"));
        b.AddDocument("c.cs", TrigramIndex.ComputeTrigrams("goodbye"));

        var path = Path.Combine(Path.GetTempPath(), "cc-seg-" + System.Guid.NewGuid().ToString("N") + ".ccseg");
        try
        {
            b.WriteTo(path);
            using var r = new SegmentReader(path);

            Assert.Equal(3, r.DocCount);
            Assert.Equal("a.cs", r.GetPath(0));
            Assert.Equal("dir/b.cs", r.GetPath(1));
            Assert.Equal("c.cs", r.GetPath(2));

            // "hel" appears in docs 0 and 1, not 2.
            Assert.Equal(new[] { 0, 1 }, r.GetPostings(Trigram("hel")));
            // "ood" (goodbye) only in doc 2.
            Assert.Equal(new[] { 2 }, r.GetPostings(Trigram("ood")));
            // A trigram not present anywhere.
            Assert.Null(r.GetPostings(Trigram("zzz")));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Postings_SurviveLargeDocCounts_AndVarintDeltas()
    {
        var b = new SegmentBuilder();
        for (int i = 0; i < 500; i++)
            b.AddDocument($"f{i}.txt", TrigramIndex.ComputeTrigrams("common token"));
        // A rare trigram only in the last doc -> a large delta from 0.
        b.AddDocument("rare.txt", TrigramIndex.ComputeTrigrams("qwx"));

        var path = Path.Combine(Path.GetTempPath(), "cc-seg-" + System.Guid.NewGuid().ToString("N") + ".ccseg");
        try
        {
            b.WriteTo(path);
            using var r = new SegmentReader(path);

            var common = r.GetPostings(Trigram("com"));
            Assert.NotNull(common);
            Assert.Equal(Enumerable.Range(0, 500).ToArray(), common);

            Assert.Equal(new[] { 500 }, r.GetPostings(Trigram("qwx")));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Varint_RoundTrips()
    {
        foreach (uint v in new uint[] { 0, 1, 127, 128, 300, 16384, uint.MaxValue })
        {
            using var ms = new MemoryStream();
            Varint.Write(ms, v);
            int offset = 0;
            Assert.Equal(v, Varint.Read(ms.ToArray(), ref offset));
            Assert.Equal((int)ms.Length, offset); // consumed exactly the bytes written
        }
    }

    [Fact]
    public void Varint_Read_Truncated_ThrowsInvalidData_NotOOB()
    {
        // A continuation-bit byte with no successor (truncated posting blob): must be a caught
        // InvalidDataException, never an IndexOutOfRangeException that could crash a query.
        int offset = 0;
        Assert.Throws<InvalidDataException>(() => Varint.Read(new byte[] { 0x80 }, ref offset));

        offset = 0;
        Assert.Throws<InvalidDataException>(() => Varint.Read(System.Array.Empty<byte>(), ref offset));
    }

    [Fact]
    public void Varint_Read_NonTerminating_ThrowsInvalidData_NotOverflow()
    {
        // Six continuation bytes (>5) is not a valid 32-bit varint: reject rather than silently
        // shift-overflow into a wrong value.
        int offset = 0;
        Assert.Throws<InvalidDataException>(
            () => Varint.Read(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 }, ref offset));
    }
}
