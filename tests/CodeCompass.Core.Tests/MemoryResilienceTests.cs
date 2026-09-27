using System;
using CodeCompass.Core.Config;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// The read budget must be gated by how much memory the machine can ACTUALLY commit right now, not just
// total RAM - sizing against total RAM over-commits when other processes hold most of the commit charge
// (a game, another build), which turned a dense-band parallel read into a fatal OutOfMemoryException.
// And however tight the budget gets, a build must still complete: an over-budget file reserves the whole
// budget and runs alone, so parallelism throttles down to serial rather than deadlocking or aborting.
[Collection("compaction-env")] // serialize the CODECOMPASS_READ_BUDGET_MB env mutation
public class MemoryResilienceTests
{
    [Fact]
    public void ReadBudget_IsCappedByAvailableCommit()
    {
        long avail = SystemMemory.AvailableCommitBytes();
        if (avail <= 0) return; // can't determine on this platform - nothing to assert

        long budget = CodeCompassConfig.ReadBudgetBytes(new RepoConfig());
        // Never admit more than half the free commit (the 256 MB floor is the one exception, for when
        // free commit is already tiny - there the per-file OOM backstop takes over).
        long ceiling = Math.Max(256L * 1024 * 1024, avail / 2);
        Assert.True(budget <= ceiling,
            $"read budget {budget / 1048576} MB exceeded the availability cap {ceiling / 1048576} MB (avail commit {avail / 1048576} MB)");
    }

    [Fact]
    public void Build_UnderTinyBudget_ThrottlesToSerial_AndCompletes()
    {
        using var repo = new TempRepo();
        const int n = 6;
        // ~2 MB each: bigger than the forced 1 MB budget, so each reserves the whole budget and runs solo.
        for (int i = 0; i < n; i++)
        {
            var sb = new System.Text.StringBuilder(2 * 1024 * 1024 + 64);
            var line = $"int unique_symbol_{i}_" + new string('x', 40) + " = 0;\n";
            while (sb.Length < 2 * 1024 * 1024) sb.Append(line);
            repo.Write($"src/file{i}.c", sb.ToString());
        }

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_READ_BUDGET_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_READ_BUDGET_MB", "1"); // 1 MB: every file exceeds it
            var (t, s, stats) = RepositoryIndexer.Build(repo.Root);
            using (t) using (s)
            {
                // The whole build completes with every file indexed - throttling to serial must not drop files.
                Assert.Equal(n, t.DocumentCount);
                Assert.True(stats.Files >= n);
            }
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_READ_BUDGET_MB", prev); }
    }
}
