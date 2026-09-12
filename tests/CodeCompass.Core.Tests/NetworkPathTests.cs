using System;
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
}
