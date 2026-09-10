using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

public class ByteBudgetTests
{
    [Fact]
    public void ConcurrentInFlightBytesNeverExceedBudget()
    {
        const long budget = 1000;
        var b = new ByteBudget(budget);

        long inFlight = 0;
        long peak = 0;
        var peakLock = new object();

        // Many workers each grabbing chunks that individually fit but would blow the budget if
        // several ran at once. The gate must keep the sum at or under the budget.
        Parallel.For(0, 200, _ =>
        {
            const long size = 300; // 3+ of these would exceed 1000
            b.Acquire(size);
            try
            {
                long now = Interlocked.Add(ref inFlight, size);
                lock (peakLock) peak = Math.Max(peak, now);
                Thread.SpinWait(200);
            }
            finally
            {
                Interlocked.Add(ref inFlight, -size);
                b.Release(size);
            }
        });

        Assert.True(peak <= budget, $"peak in-flight {peak} exceeded budget {budget}");
        Assert.Equal(0, Interlocked.Read(ref inFlight));
    }

    [Fact]
    public void FileLargerThanBudgetRunsSoloWithoutDeadlock()
    {
        var b = new ByteBudget(100);
        // A single over-budget acquire must be satisfiable (reserves the whole budget), else the
        // build would deadlock the moment it hit a file bigger than the read budget.
        b.Acquire(10_000);
        b.Release(10_000);
        // A normal acquire still works afterward (budget fully restored).
        b.Acquire(50);
        b.Release(50);
    }
}

public class BuildInfoTests
{
    [Fact]
    public void VersionStartsAtOneDotZero()
    {
        // Directory.Build.props stamps 1.0.<gitcount>[+sha]; a source drop with no git falls back
        // to the assembly version. Either way the product line is 1.0.
        Assert.StartsWith("1.0", BuildInfo.Version);
    }
}
