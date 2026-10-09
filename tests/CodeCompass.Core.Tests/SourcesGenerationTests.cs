using System;
using CodeCompass.Core.Storage;
using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// Every committed write bumps the cache's generation, so every other session reloads - and, before this, also dropped
// its resident C# analyzer (~15 s to rebuild on EF Core), even after a write that changed no source (a startup reconcile
// that found nothing, a README edit). The generation now carries a second token that moves only when sources may have
// changed; a session keeps its analyzer across a reload when that token hasn't moved.
public class SourcesGenerationTests
{
    [Fact]
    public void Commit_MovesTheSourcesToken_UnlessTheWriterSaysSourcesAreUnchanged()
    {
        using var repo = new TempRepo();
        var dir = IndexStore.GetCacheDir(repo.Root);
        string first;
        using (var wl = IndexWriteLock.Acquire(dir)) first = wl.Commit();
        var s1 = IndexGeneration.SourcesOf(first);
        Assert.False(string.IsNullOrEmpty(s1));

        string second;
        using (var wl = IndexWriteLock.Acquire(dir)) { wl.SourcesUnchanged(); second = wl.Commit(); }
        Assert.NotEqual(first, second);                       // other sessions still reload the index
        Assert.Equal(s1, IndexGeneration.SourcesOf(second));  // ...but know no source changed

        string third;
        using (var wl = IndexWriteLock.Acquire(dir)) third = wl.Commit();
        Assert.NotEqual(s1, IndexGeneration.SourcesOf(third));
    }

    [Fact]
    public void AWriteWithoutACommit_StillMovesTheSourcesToken()
    {
        using var repo = new TempRepo();
        var dir = IndexStore.GetCacheDir(repo.Root);
        string first;
        using (var wl = IndexWriteLock.Acquire(dir)) first = wl.Commit();
        using (IndexWriteLock.Acquire(dir)) { } // released without Commit: the safe default bumps everything
        Assert.NotEqual(IndexGeneration.SourcesOf(first), IndexGeneration.SourcesOf(IndexGeneration.Read(dir)));
    }

    [Theory]
    [InlineData("")]                                 // never written
    [InlineData("4effa3d6c96644c3825669e0fb19e8fc")] // written by a version without the sources token
    public void AGenerationWithoutASourcesToken_HasNone(string gen) => Assert.Null(IndexGeneration.SourcesOf(gen));

    // A reconcile that finds nothing new carries the CURRENT sources token - which may be another writer's newer one, if
    // that writer changed sources after this session built its analyzer and this session hadn't reloaded yet (its reload
    // skips while a reconcile holds the build gate). Keeping the analyzer because "this write changed nothing" left it
    // stale for good: the carried token then matched the loaded generation, so no later reload rechecked it.
    [Fact]
    public void ANoChangeReconcile_AfterAnotherWritersSourceChange_DropsTheStaleAnalyzer()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { public void Ping() { } void N() { Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("Ping"));
            using (var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(repo.Root))) wl.Commit(); // another writer: sources moved

            ServerContext.RunStartupReconcileNow(); // finds nothing new itself, carries that writer's token
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "an analyzer older than the sources token must go");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", old);
            ServerContext.Init(repo.Root);
        }
    }

    [Fact]
    public void AnotherProcessesWrite_ThatChangedNoSource_KeepsThisSessionsAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { public void Ping() { } void N() { Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("Ping"));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers());
            var dir = IndexStore.CacheDirPath(repo.Root);

            using (var wl = IndexWriteLock.Acquire(dir)) { wl.SourcesUnchanged(); wl.Commit(); } // e.g. a no-op reconcile
            CodeCompassTools.SearchCode("Ping");                                                // reloads the index
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "a write that changed no source must keep the analyzer");

            using (var wl = IndexWriteLock.Acquire(dir)) wl.Commit();                           // sources may have changed
            CodeCompassTools.SearchCode("Ping");
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a write that may have changed sources must drop it");
        }
        finally { ServerContext.Init(repo.Root); }
    }
}
