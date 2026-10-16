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

    // The build flushes a segment when ApproxBytes reaches its budget, so the estimate is what keeps a build's memory
    // inside it. It charged 5 bytes per (term, doc) pair and nothing per term - but each new term costs a dictionary
    // entry, a List object and its array (~100 bytes), so a segment of mostly-new terms (a fresh worker, source with
    // many rare identifiers) held about twice its budget. Compared against what the builder really allocates.
    [Theory]
    [InlineData(300, 2_000_000)] // mostly new terms per doc (a wide trigram space)
    [InlineData(300, 20_000)]    // terms shared by many docs (long postings)
    public void ApproxBytes_TracksWhatTheBuilderHolds(int trigramsPerDoc, int trigramSpace)
    {
        var rng = new System.Random(11);
        var docs = Enumerable.Range(0, 2000).Select(_ =>
            Enumerable.Range(0, trigramsPerDoc).Select(_ => (long)rng.Next(trigramSpace)).Distinct().ToArray()).ToList();
        var paths = Enumerable.Range(0, docs.Count).Select(i => $"src/dir{i % 50}/file{i}.cs").ToList();

        var b = new SegmentBuilder();
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < docs.Count; i++) b.AddDocument(paths[i], docs[i]);
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

        // Allocation also counts the arrays a growing List or Dictionary left behind (doubling: about as much again as the
        // final arrays), so what's held is roughly a third to all of it; the estimate must land there, not far below.
        Assert.True(b.ApproxBytes >= allocated / 3 && b.ApproxBytes <= allocated * 1.2,
                    $"ApproxBytes {b.ApproxBytes:N0} vs {allocated:N0} allocated");
    }
}
