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
}
