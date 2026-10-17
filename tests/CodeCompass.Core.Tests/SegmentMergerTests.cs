using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

// Compaction streams a k-way merge of the segments' term lists (it rebuilt every live doc's trigram list in RAM). The
// oracle is what the old code amounted to: a SegmentBuilder fed the live docs in order.
public sealed class SegmentMergerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-merge-" + Guid.NewGuid().ToString("N"));
    private readonly List<SegmentReader> _open = new();

    public SegmentMergerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var r in _open) r.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(5, 0.0)]
    [InlineData(5, 0.3)]
    [InlineData(12, 0.6)]
    public void Merge_MatchesBuildingTheLiveDocsInOneGo(int segmentCount, double deleteRate)
    {
        var rng = new Random(20261016 + segmentCount);
        var segs = new List<SegmentReader>();
        var docs = new List<List<(string Path, long[] Tris)>>();
        for (int s = 0; s < segmentCount; s++)
        {
            var b = new SegmentBuilder();
            var list = new List<(string, long[])>();
            int n = rng.Next(1, 60);
            for (int d = 0; d < n; d++)
            {
                // Keys from a small range (so segments share terms), negatives included (signed order), and some
                // multi-byte paths.
                var tris = Enumerable.Range(0, rng.Next(0, 80)).Select(_ => (long)rng.Next(-300, 300)).Distinct().ToArray();
                var path = $"dir{s}/file{d}" + (d % 7 == 0 ? "-é漢" : "") + ".cs";
                b.AddDocument(path, tris);
                list.Add((path, tris));
            }
            segs.Add(Write(b));
            docs.Add(list);
        }
        var tombs = new Dictionary<int, HashSet<int>>();
        for (int s = 0; s < segmentCount; s++)
            for (int d = 0; d < docs[s].Count; d++)
                if (rng.NextDouble() < deleteRate) (tombs.TryGetValue(s, out var t) ? t : tombs[s] = new HashSet<int>()).Add(d);

        var oracle = new SegmentBuilder();
        for (int s = 0; s < segmentCount; s++)
            for (int d = 0; d < docs[s].Count; d++)
                if (!(tombs.TryGetValue(s, out var t) && t.Contains(d))) oracle.AddDocument(docs[s][d].Path, docs[s][d].Tris);
        var oracleFile = Path.Combine(_dir, "oracle.ccseg");
        oracle.WriteTo(oracleFile);

        var mergedFile = Path.Combine(_dir, "merged.ccseg");
        int live = SegmentMerger.Merge(segs, tombs, mergedFile);
        Assert.Equal(oracle.DocCount, live);

        if (deleteRate == 0) Assert.Equal(File.ReadAllBytes(oracleFile), File.ReadAllBytes(mergedFile));
        using var want = new SegmentReader(oracleFile);
        using var got = new SegmentReader(mergedFile);
        Assert.Equal(Dump(want), Dump(got));
    }

    [Fact]
    public void Merge_WithEveryDocDeleted_WritesNothing()
    {
        var b = new SegmentBuilder();
        b.AddDocument("a.cs", new long[] { 1, 2, 3 });
        b.AddDocument("b.cs", new long[] { 2, 3, 4 });
        var seg = Write(b);
        var file = Path.Combine(_dir, "none.ccseg");
        Assert.Equal(0, SegmentMerger.Merge(new[] { seg }, new Dictionary<int, HashSet<int>> { [0] = new() { 0, 1 } }, file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Merge_DropsATermOnlyDeletedDocsHad()
    {
        var b = new SegmentBuilder();
        b.AddDocument("keep.cs", new long[] { 1, 2 });
        b.AddDocument("gone.cs", new long[] { 2, 99 });
        var file = Path.Combine(_dir, "drop.ccseg");
        SegmentMerger.Merge(new[] { Write(b) }, new Dictionary<int, HashSet<int>> { [0] = new() { 1 } }, file);
        using var r = new SegmentReader(file);
        Assert.Equal(2, r.TermCount);
        Assert.Null(r.GetPostings(99));
        Assert.Equal(new[] { 0 }, r.GetPostings(2));
        Assert.Equal("keep.cs", r.GetPath(0));
    }

    private SegmentReader Write(SegmentBuilder b)
    {
        var f = Path.Combine(_dir, $"seg-{_open.Count:D8}.ccseg");
        b.WriteTo(f);
        var r = new SegmentReader(f);
        _open.Add(r);
        return r;
    }

    private static List<string> Dump(SegmentReader r)
    {
        var lines = new List<string> { $"docs {r.DocCount} terms {r.TermCount}" };
        for (int d = 0; d < r.DocCount; d++) lines.Add("path " + r.GetPath(d));
        for (int t = 0; t < r.TermCount; t++) lines.Add($"term {r.GetTermKey(t)}: {string.Join(',', r.GetPostingsAt(t))}");
        return lines;
    }
}
