using System;
using System.Collections.Generic;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// The parsed positional sidecar (block table + per-block Blooms) is cached in-process, so the long-lived
// server reads the whole Bloom payload of a large file ONCE, not on every query. The field report measured
// ~695 MB of local sidecar re-reads per broad query (65% of all bytes); caching turns that into read-once.
// The search trace reports the TRUE sidecar bytes read per call (0 on a cache hit) so the win is observable.
[Collection("compaction-env")] // serialize the CODECOMPASS_FORCE_NETWORK env mutation
public class SidecarCacheTests
{
    // ~3 MB of newline-terminated register-header text so BuildBlocks yields blocks and (over a network
    // path, adaptive 2 MB cutoff) the file gets a sidecar.
    private static string MidText()
    {
        var line = "#define HWIO_" + new string('Q', 40) + "_ADDR 0x0000ffff\n";
        var sb = new System.Text.StringBuilder(3 * 1024 * 1024 + 128);
        while (sb.Length < 3 * 1024 * 1024) sb.Append(line);
        return sb.ToString();
    }

    [Fact]
    public void TryScan_CachesParsedSidecar_SecondScanReadsZeroSidecarBytes()
    {
        using var repo = new TempRepo();
        repo.Write("reg/map.h", MidText());

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            var (t, s, _) = RepositoryIndexer.Build(repo.Root);
            t.Dispose(); s.Dispose();

            var dir = IndexStore.GetCacheDir(repo.Root);
            const string rel = "reg/map.h";
            Assert.True(PositionalSidecar.HasSidecar(dir, rel), "the mid-size file should have a sidecar under the network cutoff");

            SidecarCache.Clear(); // start cold so the first scan is a guaranteed miss

            // First scan: cold -> reads and parses the sidecar (nonzero sidecar bytes), finds the token.
            var r1 = new List<SearchMatch>();
            bool h1 = PositionalSidecar.TryScan(dir, repo.Root, rel, "HWIO", r1, 50, caseSensitive: true, out _, out long sc1);
            Assert.True(h1);
            Assert.True(sc1 > 0, "first scan should read the sidecar from disk");
            Assert.NotEmpty(r1);

            // Second scan: the parsed sidecar is cached -> zero sidecar bytes read, identical results.
            var r2 = new List<SearchMatch>();
            bool h2 = PositionalSidecar.TryScan(dir, repo.Root, rel, "HWIO", r2, 50, caseSensitive: true, out _, out long sc2);
            Assert.True(h2);
            Assert.Equal(0, sc2); // cache hit: no sidecar I/O this query
            Assert.Equal(r1.Count, r2.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev);
            SidecarCache.Clear();
        }
    }

    [Fact]
    public void TryScan_ScanResistant_KeepsResidentEntry_WhenWorkingSetExceedsBudget()
    {
        using var repo = new TempRepo();
        repo.Write("reg/a.h", MidText());
        repo.Write("reg/b.h", MidText());

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            var (t, s, _) = RepositoryIndexer.Build(repo.Root);
            t.Dispose(); s.Dispose();
            var dir = IndexStore.GetCacheDir(repo.Root);
            Assert.True(PositionalSidecar.HasSidecar(dir, "reg/a.h"));
            Assert.True(PositionalSidecar.HasSidecar(dir, "reg/b.h"));

            // Budget holds exactly ONE of the two sidecars (sized off the real sidecar length so it's robust
            // to block count). This is the field-report pathology in miniature: a working set bigger than the
            // budget. A plain LRU would evict A to admit B, then thrash to 0 hits on the repeat; skip-when-full
            // keeps A resident so it still hits.
            long scLen = PositionalSidecar.SidecarLength(dir, "reg/a.h");
            Assert.True(scLen > 0, "the mid-size file should have a sidecar on disk");
            SidecarCache.ResetForTest((long)(scLen * 1.5)); // fits one (~1.0x), not two (~2.0x)
            try
            {
                var r1 = new List<SearchMatch>();
                PositionalSidecar.TryScan(dir, repo.Root, "reg/a.h", "HWIO", r1, 50, caseSensitive: true, out _, out long a1); // miss -> cached
                var rb = new List<SearchMatch>();
                PositionalSidecar.TryScan(dir, repo.Root, "reg/b.h", "HWIO", rb, 50, caseSensitive: true, out _, out long bb); // miss -> over budget, NOT cached
                var r2 = new List<SearchMatch>();
                PositionalSidecar.TryScan(dir, repo.Root, "reg/a.h", "HWIO", r2, 50, caseSensitive: true, out _, out long a2); // A still resident -> HIT

                Assert.True(a1 > 0, "first scan of A reads its sidecar");
                Assert.True(bb > 0, "B is read from disk (it didn't fit the cache)");
                Assert.Equal(0, a2); // scan-resistant: A survived B's oversized scan and hits (LRU would miss here)
            }
            finally { SidecarCache.ResetForTest(256L * 1024 * 1024); } // restore a sane budget for other tests
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev); }
    }

    // The RAM-scaled cache can hold up to ~2 GB. When the machine goes tight (a concurrent build, a clang
    // semantic pass) that resident set becomes dead weight. Under pressure the cache must give those bytes
    // back and stop admitting - it's a read accelerator, not a correctness requirement. The pressure verdict
    // is forced deterministically here so no gigabytes are allocated to trip a real threshold.
    [Fact]
    public void TryScan_ReleasesResidentAndSkipsAdmission_UnderMemoryPressure()
    {
        using var repo = new TempRepo();
        repo.Write("reg/a.h", MidText());
        repo.Write("reg/b.h", MidText());

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            var (t, s, _) = RepositoryIndexer.Build(repo.Root);
            t.Dispose(); s.Dispose();
            var dir = IndexStore.GetCacheDir(repo.Root);

            SidecarCache.ResetForTest(2L * 1024 * 1024 * 1024); // generous budget so admission isn't the gate
            try
            {
                // No pressure: A is admitted and becomes resident.
                var r1 = new List<SearchMatch>();
                PositionalSidecar.TryScan(dir, repo.Root, "reg/a.h", "HWIO", r1, 50, caseSensitive: true, out _, out long a1);
                Assert.True(a1 > 0, "cold scan of A reads its sidecar");
                Assert.True(SidecarCache.ResidentBytes > 0, "A should be cached when there's no pressure");

                // Now force pressure. The next miss (B) releases the resident set and does not admit B.
                SidecarCache.PressureProbeForTest = () => true;
                long releasesBefore = SidecarCache.ReleaseCount;
                var rb = new List<SearchMatch>();
                PositionalSidecar.TryScan(dir, repo.Root, "reg/b.h", "HWIO", rb, 50, caseSensitive: true, out _, out long bb);
                Assert.True(bb > 0, "B is read from disk");
                Assert.Equal(0, SidecarCache.ResidentBytes); // the cache gave its bytes back
                Assert.True(SidecarCache.ReleaseCount > releasesBefore, "a release should have been recorded");

                // Still under pressure: a re-scan of A is a MISS (it was released) and B was never admitted.
                var r2 = new List<SearchMatch>();
                PositionalSidecar.TryScan(dir, repo.Root, "reg/a.h", "HWIO", r2, 50, caseSensitive: true, out _, out long a2);
                Assert.True(a2 > 0, "A was released, so this is a miss that re-reads its sidecar");
                Assert.Equal(0, SidecarCache.ResidentBytes); // still refusing to admit under pressure

                // Pressure clears: admission resumes.
                SidecarCache.PressureProbeForTest = null;
                var r3 = new List<SearchMatch>();
                PositionalSidecar.TryScan(dir, repo.Root, "reg/a.h", "HWIO", r3, 50, caseSensitive: true, out _, out long a3);
                Assert.True(SidecarCache.ResidentBytes > 0, "once pressure clears, A is cached again");
            }
            finally { SidecarCache.ResetForTest(256L * 1024 * 1024); } // restore a sane budget + clear the probe
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev); }
    }
}
