using System;
using System.IO;
using System.Threading;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Symbols;
using Xunit;

namespace CodeCompass.Core.Tests;

public class WatcherTests
{
    [Fact]
    public void Watcher_ReindexesOnFileChange()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Existing { } }");
        RepositoryIndexer.Build(repo.Root);

        SymbolIndex? latest = null;
        using var reindexed = new ManualResetEventSlim(false);

        using var watcher = new RepositoryWatcher(repo.Root, batch =>
        {
            var (_, symbols, _) = batch.FullReconcile
                ? RepositoryIndexer.Update(repo.Root)
                : RepositoryIndexer.UpdatePaths(repo.Root, batch.ChangedFullPaths);
            latest = symbols;
            reindexed.Set();
        }, debounceMs: 200);
        watcher.Start();

        // Add a new file; the watcher should notice and incrementally reindex.
        repo.Write("b.cs", "namespace N { class FreshlyAdded { } }");

        Assert.True(reindexed.Wait(TimeSpan.FromSeconds(15)), "watcher did not fire a reindex in time");
        Assert.NotNull(latest);
        Assert.NotEmpty(latest!.FindByName("FreshlyAdded"));
    }

    [Fact]
    public void Watcher_IgnoresChangesUnderIgnoredDirectories()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");
        RepositoryIndexer.Build(repo.Root);

        using var fired = new ManualResetEventSlim(false);
        using var watcher = new RepositoryWatcher(repo.Root, _ => fired.Set(), debounceMs: 200);
        watcher.Start();

        // Writing into an ignored directory (bin/) must NOT trigger a reindex.
        repo.Write("bin/generated.cs", "namespace N { class ShouldBeIgnored { } }");

        Assert.False(fired.Wait(TimeSpan.FromSeconds(3)), "watcher reacted to an ignored directory");
    }
}
