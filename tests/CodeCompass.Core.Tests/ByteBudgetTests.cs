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
    public void VersionIsTheCommitDate()
    {
        // Directory.Build.props stamps <year>.<month>.<day>.<n>+<sha>: HEAD's commit date (no leading zeros) and its
        // number that day. A source drop with no git gets 0.0.0.0+nogit.
        var m = System.Text.RegularExpressions.Regex.Match(BuildInfo.Version, @"^(\d{4}\.\d{1,2}\.\d{1,2})\.(\d+)\+([0-9a-f]{8})$");
        if (BuildInfo.Version == "0.0.0.0+nogit") return;
        Assert.True(m.Success, BuildInfo.Version);
        Assert.True(int.Parse(m.Groups[2].Value) >= 1, BuildInfo.Version);

        var psi = new System.Diagnostics.ProcessStartInfo("git", "log -1 --format=%cd --date=format:%Y.%m.%d " + m.Groups[3].Value)
        {
            WorkingDirectory = System.AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        string day;
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            day = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode != 0 || day.Length == 0) return; // no git here: the shape is all that can be checked
        }
        catch (System.ComponentModel.Win32Exception) { return; }
        Assert.Equal(System.Version.Parse(day + ".0").ToString(3), m.Groups[1].Value);
    }
}
