using System;
using System.Collections.Generic;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// The adaptive, landscape-driven sidecar threshold: over a network path with a real 1-8 MB tail (the
// firmware / register-map / debugger-script shape), the build lowers the sidecar cutoff 8 MB -> 2 MB so
// those trigram-dense mid-size files get block-selective reads instead of whole-file reads on broad queries.
// Local repos keep 8 MB. The chosen cutoff is recorded in meta so incremental updates stay consistent.
[Collection("compaction-env")] // serialize the CODECOMPASS_FORCE_NETWORK env mutation
public class AdaptiveLandscapeTests
{
    // ~3 MB of newline-terminated, register-header-shaped text so BuildBlocks (line-aligned) yields blocks.
    private static string MidText(char seed)
    {
        var line = "#define HWIO_" + new string(seed, 40) + "_ADDR 0x0000ffff\n";
        var sb = new System.Text.StringBuilder(3 * 1024 * 1024 + 128);
        while (sb.Length < 3 * 1024 * 1024) sb.Append(line);
        return sb.ToString();
    }

    [Fact]
    public void Threshold_IsNetworkGated_AndMidTailGated()
    {
        // A shape with a fat 1-8 MB tail: 1200 files @ 3 MB + a few tiny.
        var sizes = new List<long>();
        for (int i = 0; i < 1200; i++) sizes.Add(3L * 1024 * 1024);
        for (int i = 0; i < 100; i++) sizes.Add(500);
        var midHeavy = RepoLandscape.Compute(sizes);
        Assert.True(midHeavy.MidTailFiles >= 1000);
        Assert.True(midHeavy.MidTailByteShare > 0.9);
        Assert.Equal(RepoLandscape.LowSidecarThreshold, midHeavy.EffectiveSidecarThreshold(isNetwork: true));   // network -> 2 MB
        Assert.Equal(RepoLandscape.DefaultSidecarThreshold, midHeavy.EffectiveSidecarThreshold(isNetwork: false)); // local -> 8 MB

        // A normal source repo: all tiny -> no mid-tail -> keep 8 MB even over a network path.
        var tiny = new List<long>();
        for (int i = 0; i < 5000; i++) tiny.Add(4096);
        var normal = RepoLandscape.Compute(tiny);
        Assert.Equal(RepoLandscape.DefaultSidecarThreshold, normal.EffectiveSidecarThreshold(isNetwork: true));
    }

    [Fact]
    public void AdaptiveBuild_NetworkWithMidTail_SidecarsMidSizeFiles_AndPersistsThreshold()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 4; i++) repo.Write($"mid/reg{i}.h", MidText((char)('a' + i))); // ~3 MB each -> mid-tail
        repo.Write("small.c", "int x;\n");

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            var (t, s, _) = RepositoryIndexer.Build(repo.Root);
            t.Dispose(); s.Dispose();

            var dir = IndexStore.GetCacheDir(repo.Root);
            // A 3 MB file is below the old 8 MB cutoff but at/above the adaptive 2 MB one -> it now has a sidecar.
            Assert.True(PositionalSidecar.HasSidecar(dir, "mid/reg0.h"),
                "a 3 MB file on a network+mid-tail repo should get a sidecar under the lowered cutoff");
            var meta = IndexMetaFile.Read(repo.Root);
            Assert.NotNull(meta);
            Assert.Equal(RepoLandscape.LowSidecarThreshold, meta!.SidecarThresholdBytes); // recorded for incremental consistency
            Assert.False(string.IsNullOrEmpty(meta.Landscape));
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev); }
    }

    [Fact]
    public void AdaptiveBuild_Local_KeepsDefaultCutoff_NoMidSizeSidecar()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 4; i++) repo.Write($"mid/reg{i}.h", MidText((char)('a' + i)));

        var (t, s, _) = RepositoryIndexer.Build(repo.Root); // local (no force-network)
        t.Dispose(); s.Dispose();

        var dir = IndexStore.GetCacheDir(repo.Root);
        Assert.False(PositionalSidecar.HasSidecar(dir, "mid/reg0.h"),
            "locally the 8 MB cutoff stands - a 3 MB file gets no sidecar (whole-file reads are ~free locally)");
        Assert.Equal(RepoLandscape.DefaultSidecarThreshold, IndexMetaFile.Read(repo.Root)!.SidecarThresholdBytes);
    }

    [Fact]
    public void IncrementalUpdate_PreservesAdaptiveSidecar()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 4; i++) repo.Write($"mid/reg{i}.h", MidText((char)('a' + i)));

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
            t0.Dispose(); s0.Dispose();
            var dir = IndexStore.GetCacheDir(repo.Root);
            Assert.True(PositionalSidecar.HasSidecar(dir, "mid/reg0.h"));

            // Edit a mid-size file (still ~3 MB). An incremental update that used the WRONG (8 MB) cutoff would
            // hit the reconcile's else-branch and DELETE the sidecar. Using the persisted 2 MB cutoff, it
            // rewrites it - so the sidecar must survive the edit.
            repo.Write("mid/reg0.h", MidText('z'));
            var (t1, s1, _) = RepositoryIndexer.Update(repo.Root);
            t1.Dispose(); s1.Dispose();

            Assert.True(PositionalSidecar.HasSidecar(dir, "mid/reg0.h"),
                "an incremental update must keep the adaptive sidecar (it must reuse the build's 2 MB cutoff, not reset to 8 MB)");
            Assert.Equal(RepoLandscape.LowSidecarThreshold, IndexMetaFile.Read(repo.Root)!.SidecarThresholdBytes); // still preserved
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev); }
    }
}
