using System;
using System.Linq;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Shares the process-global CODECOMPASS_FORCE_NETWORK env with McpToolsTests' network-gating test, so
// it lives in the same serialized collection (env is process-wide; parallel classes would race on it).
[Collection("compaction-env")]
public class NetworkPathTests
{
    [Fact]
    public void UncPath_IsNetwork()
    {
        Assert.True(NetworkPath.IsNetwork(@"\\fileserver\share\repo"));
        Assert.True(NetworkPath.IsNetwork(@"\\10.0.0.5\team\proj\src"));
    }

    [Fact]
    public void LocalPath_IsNotNetwork()
    {
        // The test working directory is on a local fixed drive.
        Assert.False(NetworkPath.IsNetwork(AppContext.BaseDirectory));
        Assert.False(NetworkPath.IsNetwork(""));
    }

    [Fact]
    public void ForceNetworkEnv_OverridesDetection()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            // Force ON: a plainly-local path is treated as network (the test fake-out / DFS escape hatch).
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            Assert.True(NetworkPath.IsNetwork(AppContext.BaseDirectory));

            // Force OFF: even a UNC path is treated as local.
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "0");
            Assert.False(NetworkPath.IsNetwork(@"\\fileserver\share\repo"));

            // Unrecognized value -> falls back to detection (UNC still true).
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "maybe");
            Assert.True(NetworkPath.IsNetwork(@"\\fileserver\share\repo"));
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", old); }
    }

    // Linux/macOS: an NFS/CIFS/SMB mount is a path like any other, so it was treated as local and got the 3 s ledger margin
    // against the SERVER's clock (review finding 5). The deepest mount containing the path decides.
    private static readonly string[] MountRoots = { "/", "/mnt/nas", "/mnt/nas/scratch", "/Volumes/Team Share", "/net/hung" };
    private static readonly string[] NetworkRoots = { "/mnt/nas", "/Volumes/Team Share", "/net/hung" };

    [Theory]
    [InlineData("/mnt/nas", true)]
    [InlineData("/mnt/nas/repo/a.c", true)]
    [InlineData("/mnt/nasty/repo", false)]          // a prefix of the name, not a parent directory
    [InlineData("/mnt/nas/scratch/x.c", false)]     // a local mount nested under the share
    [InlineData("/home/u/repo", false)]
    [InlineData("/Volumes/Team Share/repo", true)]
    public void UnixMountTable_DeepestMountDecides(string path, bool network) =>
        Assert.Equal(network, NetworkPath.OnNetworkMount(path, MountRoots, r => NetworkRoots.Contains(r)));

    // Review round 3: asking a mount for its type is a statfs, which blocks on a hung hard-mounted NFS export (or wakes an
    // automount). Only the ONE mount that contains the path may be asked - never an unrelated one.
    [Fact]
    public void UnixMountTable_AsksOnlyTheContainingMount()
    {
        var asked = new System.Collections.Generic.List<string>();
        NetworkPath.OnNetworkMount("/home/u/repo/a.c", MountRoots, r => { asked.Add(r); return NetworkRoots.Contains(r); });
        Assert.Equal(new[] { "/" }, asked);
    }
}
