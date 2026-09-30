using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// Re-point epoch guard (deferred hardening #1): a background build/reconcile that started for the PREVIOUS
// workspace must never install its result after Init() re-pointed the server - that would silently serve the
// wrong repository. Shares the compaction-env collection so it runs serially against the other tests that
// drive ServerContext's static state.
[Collection("compaction-env")]
public class RepointEpochTests
{
    [Fact]
    public void Swap_WithStaleEpoch_IsDiscarded_CurrentEpochInstalls()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");

        ServerContext.Init(repo.Root);
        int staleEpoch = ServerContext.EpochForTest;

        ServerContext.Init(repo.Root); // re-point -> bumps the epoch; a build begun at staleEpoch is now stale
        Assert.NotEqual(staleEpoch, ServerContext.EpochForTest);

        // A result carrying the stale epoch must be discarded (and its handles disposed), NOT installed.
        var stale = RepositoryIndexer.Build(repo.Root);
        Assert.False(ServerContext.SwapForTest(stale.Text, stale.Symbols, staleEpoch));
        Assert.False(ServerContext.IsServingForTest); // nothing installed from a stale build

        // A result carrying the current epoch installs normally.
        var fresh = RepositoryIndexer.Build(repo.Root);
        Assert.True(ServerContext.SwapForTest(fresh.Text, fresh.Symbols, ServerContext.EpochForTest));
        Assert.True(ServerContext.IsServingForTest);

        ServerContext.Init(repo.Root); // leave clean for the next test in the collection (disposes what we installed)
    }
}
