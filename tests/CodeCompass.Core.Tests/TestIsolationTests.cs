using System.Reflection;
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
}