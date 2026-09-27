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

        var scratch = new HashSet<long>();
        // Warm up both paths so steady-state allocation is measured, not first-call JIT.
        _ = TrigramIndex.ComputeTrigrams(text, scratch);
        _ = TrigramIndex.ComputeTrigrams(text);

        const int iters = 500;
        long reused = Alloc(() => { for (int i = 0; i < iters; i++) _ = TrigramIndex.ComputeTrigrams(text, scratch); });
        long fresh = Alloc(() => { for (int i = 0; i < iters; i++) _ = TrigramIndex.ComputeTrigrams(text); });

        // The scratch overload reuses one HashSet instead of allocating a fresh set (+ its bucket/entry
        // arrays) per call, so it must allocate materially less than the allocate-per-call path. Generous
        // margin (< 70%) keeps it non-flaky while still failing if the reuse is reverted (paths ~equal).
        Assert.True(reused < fresh * 0.7,
            $"scratch overload should allocate much less than the fresh-set path; " +
            $"reused={reused:N0} fresh={fresh:N0} bytes over {iters} calls");
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
}
