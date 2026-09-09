using System;
using CodeCompass.Core.Diagnostics;
using Xunit;

namespace CodeCompass.Core.Tests;

public class ProcessPerformanceTests
{
    [Fact]
    public void RequestFullSpeed_ActuallyOptsOutOfExecutionSpeedThrottling()
    {
        // Don't perturb the test runner's priority; we only care about the throttling policy.
        ProcessPerformance.RequestFullSpeed(raisePriority: false);

        var state = ProcessPerformance.ExecutionSpeedThrottlingDisabled();
        if (!OperatingSystem.IsWindows() || state is null)
            return; // OS can't report the policy (pre-Win10-1709 / non-Windows): opt-out is a safe no-op

        Assert.True(state.Value, "execution-speed (EcoQoS) throttling should be opted out after RequestFullSpeed");
    }

    [Fact]
    public void RequestFullSpeed_NeverThrows()
    {
        Assert.Null(Record.Exception(() => ProcessPerformance.RequestFullSpeed()));
        Assert.Null(Record.Exception(() => ProcessPerformance.RequestFullSpeed(raisePriority: false)));
    }
}
