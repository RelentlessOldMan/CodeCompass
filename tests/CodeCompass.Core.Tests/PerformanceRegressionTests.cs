using System;
using System.Collections.Generic;
using System.IO;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

// Perf-regression guards for the hot-path allocation work. Per the testing strategy these use RELATIVE
// comparisons / footprint-relative bounds measured with GC.GetAllocatedBytesForCurrentThread (exact per
// thread, no GC noise) - NOT wall-clock timing - so they lock in the wins without becoming machine-speed
// flaky. Each fails loudly if the optimization is reverted.
public class PerformanceRegressionTests
{
    private static long Alloc(Action a)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        a();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void ComputeTrigrams_ScratchOverload_AllocatesLessThanFreshSetPerCall()
    {
        var rnd = new Random(20260926);
        var sb = new System.Text.StringBuilder(4000);
        for (int i = 0; i < 4000; i++) sb.Append((char)('a' + rnd.Next(26)));
        var text = sb.ToString();

        var scratch = new TrigramSet();
        // Warm up both paths so steady-state allocation is measured, not first-call JIT.
        int distinct = TrigramIndex.ComputeTrigrams(text, scratch).Length;
        _ = TrigramIndex.ComputeTrigrams(text);

        const int iters = 500;
        long reused = Alloc(() => { for (int i = 0; i < iters; i++) _ = TrigramIndex.ComputeTrigrams(text, scratch); });
        long noScratch = Alloc(() => { for (int i = 0; i < iters; i++) _ = TrigramIndex.ComputeTrigrams(text); });

        // Both paths reuse a set (the no-scratch one a per-thread set: a TrigramSet holds a 256 KB table), so each call
        // allocates only its result array. A set (or its 256 KB table) built per call would blow the ceiling.
        long ceiling = iters * (distinct * 8L + 256);
        Assert.True(reused <= ceiling && noScratch <= ceiling,
            $"trigram extraction should allocate only its result; reused={reused:N0} noScratch={noScratch:N0} " +
            $"ceiling={ceiling:N0} bytes over {iters} calls");
    }

    [Fact]
    public void SegmentPostings_Decode_AllocationStaysNearResultSize()
    {
        // A segment where one trigram appears in every doc -> a long, consecutive posting list.
        const int docs = 2000;
        var b = new SegmentBuilder();
        for (int i = 0; i < docs; i++) b.AddDocument($"f{i}.txt", TrigramIndex.ComputeTrigrams("aaa"));
        long tri = TrigramIndex.ComputeTrigrams("aaa")[0];

        var path = Path.Combine(Path.GetTempPath(), "cc-perf-" + Guid.NewGuid().ToString("N") + ".ccseg");
        try
        {
            b.WriteTo(path);
            using var r = new SegmentReader(path);

            var first = r.GetPostings(tri);
            Assert.NotNull(first);
            Assert.Equal(docs, first!.Length); // correctness: every doc is listed

            const int iters = 200;
            _ = r.GetPostings(tri); // warm up
            long total = Alloc(() => { for (int i = 0; i < iters; i++) _ = r.GetPostings(tri); });
            long perCall = total / iters;

            // Count-then-fill allocates ~ the postings byte buffer (~docs bytes for delta-1 ids) plus ONE
            // exact int[docs] (docs*4). The old List<int>+ToArray() added another grow-and-copy int buffer.
            // ~8 bytes/doc passes count-then-fill and fails a revert to List+ToArray; slack covers headers.
            long ceiling = docs * 8L + 1024;
            Assert.True(perCall <= ceiling,
                $"posting decode allocated {perCall:N0} bytes/call for {docs} ids; ceiling {ceiling:N0} " +
                $"(regression to List<int>+ToArray?)");
        }
        finally { File.Delete(path); }
    }

    // A worker's scratch set kept the capacity of the largest file it ever saw (Clear() doesn't shrink): one outlier left
    // it at hundreds of MB for the rest of the build. Past the cap it is trimmed; under it, reuse is untouched.
    [Fact]
    public void TrigramScratch_IsTrimmedAfterAnOutlier_ButKeptForOrdinaryFiles()
    {
        var scratch = new TrigramSet();
        _ = TrigramIndex.ComputeTrigrams("class Foo { int bar; }", scratch);
        _ = TrigramIndex.ComputeTrigrams(new string('一', 1) + "丁丂七 x", scratch); // the non-ASCII side too
        int ordinary = scratch.RetainedCapacity;
        Assert.True(ordinary > 0); // ordinary: kept for reuse

        var rnd = new Random(20261016); // random CJK: nearly every trigram distinct
        var big = string.Create(3_000_000, rnd, (span, r) => { for (int i = 0; i < span.Length; i++) span[i] = (char)(0x4E00 + r.Next(20_000)); });
        var tg = TrigramIndex.ComputeTrigrams(big, scratch);
        Assert.True(tg.Length > TrigramIndex.ScratchTrimAbove, $"outlier has only {tg.Length} trigrams");
        Assert.Equal(tg.Length, tg.Distinct().Count());
        Assert.True(scratch.RetainedCapacity < 8192, $"scratch kept capacity {scratch.RetainedCapacity:N0}");

        // An all-ASCII outlier grows the other buffer (the first-seen list); it is trimmed the same way.
        var ascii = string.Create(4_000_000, rnd, (span, r) => { for (int i = 0; i < span.Length; i++) span[i] = (char)r.Next(128); });
        var at = TrigramIndex.ComputeTrigrams(ascii, scratch);
        Assert.True(at.Length > TrigramIndex.ScratchTrimAbove, $"ASCII outlier has only {at.Length} trigrams");
        Assert.True(scratch.RetainedCapacity < 8192, $"scratch kept capacity {scratch.RetainedCapacity:N0} after the ASCII outlier");
    }
}
