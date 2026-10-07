using System;
using System.IO;
using System.Reflection;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

public class TestIsolationTests
{
    // Many test classes mutate process-wide state (CODECOMPASS_* env vars, the static ServerContext) and are safe only
    // because the assembly runs serially. Deleting that attribute to "speed up" the suite would turn them into
    // intermittent cross-test races - fail loudly instead.
    [Fact]
    public void Suite_RunsSerially()
    {
        var behavior = typeof(TestIsolationTests).Assembly.GetCustomAttribute<CollectionBehaviorAttribute>();
        Assert.NotNull(behavior);
        Assert.True(behavior!.DisableTestParallelization);
    }

    // Field report: the suite used to leave thousands of index caches and repo-*.log files in the real
    // %LOCALAPPDATA%\CodeCompass. TestEnvironment redirects both with CODECOMPASS_CACHE_DIR / CODECOMPASS_LOG_DIR - which
    // only works while the product honours them. If it stopped, every other test would still pass and the leak would
    // quietly return.
    [Fact]
    public void CacheAndLogs_GoWhereTheEnvironmentSays_InProcessAndInTheCli()
    {
        var cacheRoot = Environment.GetEnvironmentVariable("CODECOMPASS_CACHE_DIR")!;
        var logRoot = Environment.GetEnvironmentVariable("CODECOMPASS_LOG_DIR")!;
        Assert.False(string.IsNullOrEmpty(cacheRoot));
        Assert.Equal(cacheRoot, IndexStore.BaseDir());

        using var repo = new TempRepo();
        repo.Write("a.c", "int a(void){ return 0; }\n");
        Assert.Equal(0, TestCli.Run(TestCli.Find(), "index", repo.Root).Exit);

        var key = IndexStore.RepoKey(repo.Root);
        Assert.True(Directory.Exists(Path.Combine(cacheRoot, key)), "the CLI's index is not under CODECOMPASS_CACHE_DIR");
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeCompass");
        Assert.False(Directory.Exists(Path.Combine(real, key)), "the CLI wrote this test repo's index into the real cache");
        Assert.True(Directory.Exists(logRoot) && Directory.GetFiles(logRoot, "*.log").Length > 0, "no log under CODECOMPASS_LOG_DIR");
    }
}