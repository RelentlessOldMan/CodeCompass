using System;
using CodeCompass.Core.Changes;
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
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { public void Ping() { } void N() { Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
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

    // The watcher's own writes: a README edit tells other sessions no source changed; a .cs edit tells them one did.
    [Fact]
    public void AWatchedEdit_MovesTheSourcesToken_OnlyForACSharpSource()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() { } }\n");
        repo.Write("README.md", "# readme\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            var dir = IndexStore.CacheDirPath(repo.Root);
            var before = IndexGeneration.SourcesOf(IndexGeneration.Read(dir));

            repo.Write("README.md", "# readme, edited\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("README.md") }, FullReconcile: false));
            Assert.Equal(before, IndexGeneration.SourcesOf(IndexGeneration.Read(dir)));

            repo.Write("a.cs", "class A { void M() { } void N() { M(); } }\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("a.cs") }, FullReconcile: false));
            Assert.NotEqual(before, IndexGeneration.SourcesOf(IndexGeneration.Read(dir)));
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // Same for a linked root this session writes.
    [Fact]
    public void ALinkedRootsWatchedEdit_MovesItsSourcesToken_OnlyForACSharpSource()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "class P { }\n");
        lib.Write("lib.cs", "public class Lib { public void Go() { } }\n");
        lib.Write("README.md", "# lib\n");
        var (t, s, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(lib.Root);
        t.Dispose(); s.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        var otherSession = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(lib.Root)); // no watcher here to race the test's calls
        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            CodeCompassTools.SearchCode("Lib"); // loads the linked root
            var dir = IndexStore.CacheDirPath(lib.Root);
            var before = IndexGeneration.SourcesOf(IndexGeneration.Read(dir));

            lib.Write("README.md", "# lib, edited\n");
            ServerContext.OnLinkedChangesForTest(lib.Root, new ChangeBatch(new[] { lib.FullPath("README.md") }, FullReconcile: false));
            Assert.Equal(before, IndexGeneration.SourcesOf(IndexGeneration.Read(dir)));

            lib.Write("lib.cs", "public class Lib { public void Go() { } public void Stop() { } }\n");
            ServerContext.OnLinkedChangesForTest(lib.Root, new ChangeBatch(new[] { lib.FullPath("lib.cs") }, FullReconcile: false));
            Assert.NotEqual(before, IndexGeneration.SourcesOf(IndexGeneration.Read(dir)));
        }
        finally
        {
            otherSession?.Dispose();
            ServerContext.Init(project.Root);
        }
    }

    // A session reloading a LINKED root another process wrote keeps its analyzer only while that root's sources token stays.
    [Fact]
    public void AnotherProcessesWriteToALinkedRoot_DropsTheAnalyzer_OnlyIfItsSourcesMoved()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "class P { void Use() { new Lib().Go(); } }\n");
        lib.Write("lib.cs", "public class Lib { public void Go() { } }\n");
        var (t, s, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(lib.Root);
        t.Dispose(); s.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        var otherSession = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(lib.Root));
        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("p.cs", CodeCompassTools.FindReferences("Go"));
            var dir = IndexStore.CacheDirPath(lib.Root);

            using (var wl = IndexWriteLock.Acquire(dir)) { wl.SourcesUnchanged(); wl.Commit(); } // e.g. its owner's README edit
            CodeCompassTools.SearchCode("Lib");                                                 // reloads the linked root
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "a linked write that changed no source must keep the analyzer");

            using (var wl = IndexWriteLock.Acquire(dir)) wl.Commit();                           // sources may have changed
            CodeCompassTools.SearchCode("Lib");
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a linked write that may have changed sources must drop it");
        }
        finally
        {
            otherSession?.Dispose();
            ServerContext.Init(project.Root);
        }
    }

    // The owner's own edit after another process changed sources: the reload before writing must drop the stale analyzer,
    // or the README batch's carried token (the other writer's) would mark it current for good.
    [Fact]
    public void AWatchedEdit_AfterAnotherWritersSourceChange_DropsTheStaleAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { public void Ping() { } void N() { Ping(); } }\n");
        repo.Write("README.md", "# readme\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("Ping"));
            using (var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(repo.Root))) wl.Commit(); // another writer: sources moved

            repo.Write("README.md", "# readme, edited\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("README.md") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "an analyzer older than the sources token must go");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // A C# edit that also triggers a compaction moved the sources token twice (the merge minted a second one), so a
    // session that rebuilt its model during the compaction threw it away again.
    [Fact]
    public void ACSharpEdit_ThatTriggersACompaction_MovesTheSourcesTokenOnce()
    {
        var oldCompact = Environment.GetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() { } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", "2");
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            repo.Write("a.cs", "class A { void M() { } void N() { M(); } }\n");
            int minted = IndexGeneration.SourcesMintedForTest;
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("a.cs") }, FullReconcile: false));
            Assert.Equal(1, IndexGeneration.SourcesMintedForTest - minted);
            Assert.True(ServerContext.TryGet(out var text, out _, out _));
            Assert.Equal(1, text.SegmentCount); // it did compact
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", oldCompact);
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }
}
