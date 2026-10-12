using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// Cross-process write safety (review P0-1). Before: only LINKED roots had any write guard, so two sessions on one
// repo (or a session + a terminal `codecompass index`) were unmediated writers to one cache dir - colliding segment
// numbers, last-writer-wins manifests, each side's orphan cleanup deleting the other's live segments. Now every
// mutation holds IndexWriteLock, and a process holding the index in memory reloads when another commits.
public class WriteLockTests
{
    // "Another process" committing an update: a separate thread is a distinct IndexWriteLock holder, exactly as another
    // process would be. A plain thread + join (these are synchronous APIs), rethrowing anything it hit.
    private static void OtherWriter(string root)
    {
        Exception? error = null;
        var th = new Thread(() =>
        {
            try { var u = RepositoryIndexer.Update(root); u.Text.Dispose(); u.Symbols.Dispose(); }
            catch (Exception e) { error = e; }
        });
        th.Start();
        th.Join();
        if (error is not null) throw new Exception("the other writer failed", error);
    }

    private static string TempCacheDir() =>
        Path.Combine(Path.GetTempPath(), "cc-lock-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteLock_ExcludesOtherThreads_NestsOnSameThread()
    {
        var dir = TempCacheDir();
        using var a = IndexWriteLock.TryAcquire(dir);
        Assert.NotNull(a);
        using (var nested = IndexWriteLock.TryAcquire(dir)) Assert.NotNull(nested); // Update -> Build fallback nests

        IndexWriteLock? other = null;
        var t = new Thread(() => other = IndexWriteLock.TryAcquire(dir));
        t.Start(); t.Join();
        Assert.Null(other); // a different thread (like a different process) is excluded while held
    }

    [Fact]
    public void WriteLock_ReleaseBumpsGeneration_CommitReportsIt_SkipBumpLeavesItAlone()
    {
        var dir = TempCacheDir();
        Assert.Equal("", IndexGeneration.Read(dir));

        using (IndexWriteLock.TryAcquire(dir)) { }
        var g1 = IndexGeneration.Read(dir);
        Assert.False(string.IsNullOrEmpty(g1)); // a plain release publishes a new generation

        string committed;
        using (var lk = IndexWriteLock.TryAcquire(dir)!) committed = lk.Commit();
        Assert.Equal(committed, IndexGeneration.Read(dir)); // Commit's token is exactly what readers will see
        Assert.NotEqual(g1, committed);

        using (var lk = IndexWriteLock.TryAcquire(dir)!) lk.SkipBump();
        Assert.Equal(committed, IndexGeneration.Read(dir)); // a no-op hold doesn't make other processes reload
    }

    [Fact]
    public async Task WriteLock_Acquire_WaitsForTheHolder_AndIsCancellable()
    {
        var dir = TempCacheDir();
        var holder = IndexWriteLock.TryAcquire(dir)!;
        using var acquired = new ManualResetEventSlim(false);
        var waiter = Task.Run(() => { using (IndexWriteLock.Acquire(dir)) acquired.Set(); });

        Assert.False(acquired.Wait(300)); // must NOT get in while the holder writes
        holder.Dispose();
        await waiter.WaitAsync(TimeSpan.FromSeconds(10)); // ...and proceeds once it releases
        Assert.True(acquired.IsSet);

        using var stillHeld = IndexWriteLock.TryAcquire(dir)!;
        using var cts = new CancellationTokenSource();
        var cancelled = Task.Run(() => IndexWriteLock.Acquire(dir, cts.Token));
        cts.Cancel(); // a re-point/shutdown abandons the wait instead of blocking on a long external build
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // Freshness: a session holding the index must serve what ANOTHER writer committed, not a silently stale copy.
    [Fact]
    public void Server_ReloadsWhenAnotherWriterCommits()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.SearchCode("AlphaLocal"));

            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            // Another writer (a terminal `codecompass update`) - on its own thread, so it's a distinct lock holder.
            OtherWriter(repo.Root);

            Assert.Contains("b.cs", CodeCompassTools.SearchCode("BravoExternal"));
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // The startup ignore-prune when its load fails (here: no index at all): it used to dereference the null handles, log
    // the NullReferenceException as "skipped", and - never reaching SkipBump - bump the generation on release, so every
    // other session reloaded an index nothing had written.
    [Fact]
    public void Server_PruneIgnored_WhenTheLoadFails_WritesNoGeneration()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }\n");
        ServerContext.Init(repo.Root);
        try
        {
            ServerContext.RunPruneIgnoredNow();
            Assert.Equal("", IndexGeneration.Read(IndexStore.CacheDirPath(repo.Root)));
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // The corruption case: a live-watching session applying an edit must not flush its STALE manifest over another
    // writer's newer one (which dropped that writer's segment from the index on disk).
    [Fact]
    public void Server_IncrementalAfterExternalWrite_KeepsTheOtherWritersSegments()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            OtherWriter(repo.Root);

            repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));

            // What's ON DISK (a fresh process's view) must hold both writers' work.
            Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
            using (t) using (s)
            {
                Assert.NotEmpty(t.Search("BravoExternal"));
                Assert.NotEmpty(t.Search("CharlieLocal"));
            }
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // When another writer's commit can't be reloaded right now (a transient lock here; also an index from a newer
    // CodeCompass), a watched edit was applied to this session's STALE copy and persisted anyway - overwriting that
    // writer's segment - and committed as current, so the reload was never retried. It must be held back instead, and
    // land once the reload works.
    [Fact]
    public void Server_EditWhileAnotherWritersIndexCantBeReloaded_IsHeldBack_Disclosed_AndRetried()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { public class AlphaLocal { public void Ping() { } } }");
        repo.Write("u.cs", "namespace N { class U { void M(AlphaLocal a) { a.Ping(); } } }");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            Assert.Contains("u.cs", CodeCompassTools.FindReferences("Ping")); // a resident C# model
            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            OtherWriter(repo.Root);

            repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
            var symbolsManifest = Path.Combine(IndexStore.CacheDirPath(repo.Root), "symbols.manifest");
            using (new FileStream(symbolsManifest, FileMode.Open, FileAccess.Read, FileShare.None)) // this session can't reload now
            {
                ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));
                Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "the files changed on disk: the model that read them goes");
                Assert.Contains("aren't in the index yet", CodeCompassTools.SearchCode("AlphaLocal")); // said, not silent
            }

            Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
            using (t) using (s) Assert.NotEmpty(t.Search("BravoExternal")); // the other writer's work is intact on disk

            // Reloadable again: the retry lands the held-back edit by itself - no query or edit from this session needed.
            bool landed = false;
            for (int i = 0; i < 150 && !landed; i++)
            {
                Thread.Sleep(100);
                if (RepositoryIndexer.TryLoad(repo.Root, out var t2, out var s2))
                    using (t2) using (s2) landed = t2.Search("CharlieLocal").Count > 0;
            }
            Assert.True(landed, "the held-back edit never landed");
            Assert.DoesNotContain("aren't in the index yet", CodeCompassTools.SearchCode("CharlieLocal"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A newer CodeCompass rewrote the index mid-session (another install updated first): this session can neither read nor
    // write it. It held edits back forever, kept answering from its old copy as if current, and kept the live-watch role, so
    // the newer session couldn't take over. It must say why, let Grep run, never write over it, and hand the role back.
    [Fact]
    public void Server_AnIndexANewerCodeCompassRewrote_IsNotServedStale_WrittenOver_OrKeptFromIt()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            // No live watcher: it would race the test's own edits below (and map the segment patched next). The role is
            // held from Init all the same.
            CodeCompassTools.Reindex();
            Assert.True(ServerContext.IsLiveWatchOwnerForTest);
            var before = Directory.GetFiles(IndexStore.CacheDirPath(repo.Root), "seg-*.ccseg");
            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            OtherWriter(repo.Root);
            var newest = Directory.GetFiles(IndexStore.CacheDirPath(repo.Root), "seg-*.ccseg").Except(before).First();
            using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Write)) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }
            var bytes = File.ReadAllBytes(newest); // now it reads as a newer format's

            repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));

            Assert.Contains("NEWER CodeCompass", CodeCompassTools.SearchCode("AlphaLocal")); // not the old copy, as if current
            Assert.False(ServerLiveness.IsServed(repo.Root));                               // so Grep runs
            Assert.Equal(bytes, File.ReadAllBytes(newest));                                  // nothing written over it
            for (int i = 0; i < 50 && ServerContext.IsLiveWatchOwnerForTest; i++) Thread.Sleep(100);
            Assert.False(ServerContext.IsLiveWatchOwnerForTest);                             // free for a session that can write it
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // ...and once this version's index is back (the cache purged and rebuilt, or another session of this version rebuilt it),
    // the session must take the live-watch role back. Only a reload cleared the newer-format state, not a rebuild, so it
    // never watched again - edits went unindexed with no note.
    [Fact]
    public void Server_AfterANewerFormatIndexIsRebuiltByThisVersion_TheLiveWatchRoleIsTakenBack()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        var cache = IndexStore.CacheDirPath(repo.Root);
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            var before = Directory.GetFiles(cache, "seg-*.ccseg");
            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            OtherWriter(repo.Root);
            var newest = Directory.GetFiles(cache, "seg-*.ccseg").Except(before).First();
            using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Write)) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("a.cs") }, FullReconcile: false));
            for (int i = 0; i < 50 && ServerContext.IsLiveWatchOwnerForTest; i++) Thread.Sleep(100);
            Assert.False(ServerContext.IsLiveWatchOwnerForTest);

            var wl = IndexWriteLock.Acquire(cache);
            var b = RepositoryIndexer.Build(repo.Root, overNewerFormat: true);
            wl.Commit(); wl.Dispose();
            Assert.True(ServerContext.SwapForTest(b.Text, b.Symbols, ServerContext.EpochForTest));

            Assert.Contains("BravoExternal", CodeCompassTools.SearchCode("BravoExternal")); // a query: the role is free to take
            for (int i = 0; i < 50 && !ServerContext.IsLiveWatchOwnerForTest; i++) Thread.Sleep(100);
            Assert.True(ServerContext.IsLiveWatchOwnerForTest, "the session never took the live-watch role back");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A corrupt index on disk is rebuilt by the held-back edits' retry. Each failing edit re-armed that retry with a longer
    // delay, so while edits kept coming faster than the back-off it never ran: the index stayed broken and every edit held.
    [Fact]
    public void Server_ACorruptIndexIsRebuilt_EvenWhileEditsKeepComing()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        var cache = IndexStore.CacheDirPath(repo.Root);
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "true"); // a local repo: the rebuild runs in-session
            var before = Directory.GetFiles(cache, "seg-*.ccseg");
            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            OtherWriter(repo.Root);
            var newest = Directory.GetFiles(cache, "seg-*.ccseg").Except(before).First();
            using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Write)) fs.Write(new byte[4]); // corrupt (no magic)

            bool rebuilt = false;
            for (int i = 0; i < 24 && !rebuilt; i++) // an edit every 250 ms for 6 s
            {
                repo.Write("c.cs", $"namespace N {{ class CharlieLocal{i} {{ }} }}");
                ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));
                Thread.Sleep(250);
                if (RepositoryIndexer.TryLoad(repo.Root, out var t, out var s))
                    using (t) using (s) rebuilt = t.Search("BravoExternal").Count > 0;
            }
            Assert.True(rebuilt, "the corrupt index was never rebuilt while edits kept coming");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A rebuild a hold queued (the index was corrupt) found a NEWER CodeCompass's index by the time it ran: it went ahead,
    // Build refused, and the session said "a rebuild failed" while keeping its Grep marker and the live-watch role. It must
    // leave the index alone and hand over, as for any newer-format index.
    [Fact]
    public void Server_AHoldsRebuild_FindingANewerFormatIndex_HandsOverInsteadOfRebuilding()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        var cache = IndexStore.CacheDirPath(repo.Root);
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "true");
            var before = Directory.GetFiles(cache, "seg-*.ccseg");
            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            OtherWriter(repo.Root);
            var newest = Directory.GetFiles(cache, "seg-*.ccseg").Except(before).First();
            var orig = File.ReadAllBytes(newest);
            using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Write)) fs.Write(new byte[4]); // corrupt: a rebuild is queued
            repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));

            // Before the retry (1 s) runs, a newer CodeCompass rewrites it.
            BitConverter.GetBytes(99).CopyTo(orig, 4);
            using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) fs.Write(orig, 0, 8);
            Thread.Sleep(2500); // no query meanwhile: the retry alone must notice

            Assert.False(ServerLiveness.IsServed(repo.Root), "the session kept its Grep marker over a newer-format index");
            Assert.Equal(orig, File.ReadAllBytes(newest));
            var r = CodeCompassTools.SearchCode("AlphaLocal");
            Assert.Contains("NEWER CodeCompass", r);
            Assert.DoesNotContain("a rebuild of the index failed", r);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A rebuild that ended a hold left the held paths recorded, so a later hold of the same file didn't count as a new edit
    // and kept a C# model that had read the old file.
    [Fact]
    public void Server_AnEditHeldAfterARebuildEndedAHold_StillDropsTheCSharpModel()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { public class AlphaLocal { public void Ping() { } } }");
        repo.Write("u.cs", "namespace N { class U { void M(AlphaLocal a) { a.Ping(); } } }");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        var cache = IndexStore.CacheDirPath(repo.Root);
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "true");
            var before = Directory.GetFiles(cache, "seg-*.ccseg");
            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            OtherWriter(repo.Root);
            var newest = Directory.GetFiles(cache, "seg-*.ccseg").Except(before).First();
            using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Write)) fs.Write(new byte[4]);
            repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false)); // held; rebuilt by the retry
            bool rebuilt = false; // served by this session (its rebuild swapped in), not only on disk
            for (int i = 0; i < 100 && !rebuilt; i++) { Thread.Sleep(100); rebuilt = CodeCompassTools.SearchCode("CharlieLocal").Contains("c.cs"); }
            Assert.True(rebuilt);
            Assert.Contains("u.cs", CodeCompassTools.FindReferences("Ping")); // a model built since
            Assert.True(ServerContext.HasResidentSemanticAnalyzers());

            using (var wl = IndexWriteLock.Acquire(cache)) { wl.SourcesUnchanged(); wl.Commit(); } // another writer, no source changed
            repo.Write("c.cs", "namespace N { class CharlieLocal { void Edited() { } } }");
            var symbolsManifest = Path.Combine(cache, "symbols.manifest");
            using (new FileStream(symbolsManifest, FileMode.Open, FileAccess.Read, FileShare.None))
                ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a newly held .cs edit must drop the model that read the old file");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A held-back edit that can't touch the C# model (a README) mustn't throw it away - nor must each retry of it.
    [Fact]
    public void Server_AHeldBackReadmeEdit_KeepsTheCSharpModel()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { public class AlphaLocal { public void Ping() { } } }");
        repo.Write("u.cs", "namespace N { class U { void M(AlphaLocal a) { a.Ping(); } } }");
        repo.Write("README.md", "# readme\n");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            Assert.Contains("u.cs", CodeCompassTools.FindReferences("Ping"));
            using (var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(repo.Root))) { wl.SourcesUnchanged(); wl.Commit(); } // another writer, no source changed

            repo.Write("README.md", "# readme, edited\n");
            var symbolsManifest = Path.Combine(IndexStore.CacheDirPath(repo.Root), "symbols.manifest");
            using (new FileStream(symbolsManifest, FileMode.Open, FileAccess.Read, FileShare.None))
                ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("README.md") }, FullReconcile: false));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "a held-back README edit must keep the C# model");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // ...but when the writer whose commit can't be reloaded DID change sources (its generation says so even though its index
    // won't load), the model is stale whatever the held edit was.
    [Fact]
    public void Server_AHeldBackEdit_AfterAnotherWritersSourceChange_DropsTheCSharpModel()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { public class AlphaLocal { public void Ping() { } } }");
        repo.Write("u.cs", "namespace N { class U { void M(AlphaLocal a) { a.Ping(); } } }");
        repo.Write("README.md", "# readme\n");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            Assert.Contains("u.cs", CodeCompassTools.FindReferences("Ping"));
            using (var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(repo.Root))) wl.Commit(); // another writer: sources moved

            repo.Write("README.md", "# readme, edited\n");
            var symbolsManifest = Path.Combine(IndexStore.CacheDirPath(repo.Root), "symbols.manifest");
            using (new FileStream(symbolsManifest, FileMode.Open, FileAccess.Read, FileShare.None))
                ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("README.md") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "an analyzer older than the sources token must go");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // The rebuild a hold queued (another writer left the index corrupt on disk) failing too: its edits are missing, and the
    // "retrying" note would never clear. Each query must say what's wrong and how to fix it instead.
    [Fact]
    public void Server_ARebuildThatFails_SaysSo_InsteadOfRetryingForever()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        var cache = IndexStore.CacheDirPath(repo.Root);
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "true"); // a local repo: the rebuild runs in-session
            var before = Directory.GetFiles(cache, "seg-*.ccseg");
            repo.Write("b.cs", "namespace N { class BravoExternal { } }");
            OtherWriter(repo.Root);
            var newest = Directory.GetFiles(cache, "seg-*.ccseg").Except(before).First();
            using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Write)) fs.Write(new byte[4]); // the index on disk is corrupt (no magic)...
            RepositoryIndexer.OnFileStartForTests = _ => throw new IOException("simulated: the disk is full"); // ...and no rebuild finishes

            repo.Write("c.cs", "namespace N { class CharlieLocal { } }");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false));

            string r = "";
            for (int i = 0; i < 100 && !(r = CodeCompassTools.SearchCode("AlphaLocal")).Contains("a rebuild of the index failed"); i++) Thread.Sleep(100);
            Assert.Contains("a rebuild of the index failed", r);
            Assert.Contains("codecompass update", r);
            Assert.DoesNotContain("aren't in the index yet", r);
        }
        finally
        {
            RepositoryIndexer.OnFileStartForTests = null;
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A linked root a NEWER CodeCompass re-indexed: this session can't write it. It retried forever, every retry moving the
    // root's generation (so every session dropped its C# model), and kept the root's live-watch role from sessions that can.
    [Fact]
    public void Server_ALinkedRootANewerCodeCompassRewrote_IsHandedBack_NotRetriedForever()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "namespace P { class ProjectProbe { } }");
        lib.Write("l.cs", "namespace L { class LinkedProbe { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(lib.Root);
        t0.Dispose(); s0.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        var libCache = IndexStore.CacheDirPath(lib.Root);
        ServerContext.Init(project.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            Assert.Contains("LinkedProbe", CodeCompassTools.SearchCode("LinkedProbe"));
            Assert.True(ServerContext.IsLinkedOwnerForTest(lib.Root));

            // Another install re-indexes it (a rebuild: no file under the root changes, so this session's watcher stays quiet)...
            var before = Directory.GetFiles(libCache, "seg-*.ccseg");
            var other = new Thread(() => { var b = RepositoryIndexer.Build(lib.Root); b.Text.Dispose(); b.Symbols.Dispose(); });
            other.Start(); other.Join();
            var newest = Directory.GetFiles(libCache, "seg-*.ccseg").Except(before).First();
            // ...in a format this one can't read.
            using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Write)) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }

            ServerContext.OnLinkedChangesForTest(lib.Root, new ChangeBatch(new[] { lib.FullPath("late.cs") }, FullReconcile: false));

            for (int i = 0; i < 50 && ServerContext.IsLinkedOwnerForTest(lib.Root); i++) Thread.Sleep(100);
            Assert.False(ServerContext.IsLinkedOwnerForTest(lib.Root));                   // handed back
            Assert.Contains("NEWER CodeCompass", CodeCompassTools.SearchCode("ProjectProbe")); // and said
            var gen = IndexGeneration.Read(libCache);
            Thread.Sleep(3500);                                                             // past a few retries' worth
            Assert.Equal(gen, IndexGeneration.Read(libCache));                              // nothing keeps rewriting it

            // Re-indexed by THIS version, it's readable again: no longer given up, and this session can take it over again.
            var fixer = new Thread(() => { var b = RepositoryIndexer.Build(lib.Root, overNewerFormat: true); b.Text.Dispose(); b.Symbols.Dispose(); });
            fixer.Start(); fixer.Join();
            string r = "";
            for (int i = 0; i < 50 && (r = CodeCompassTools.SearchCode("ProjectProbe")).Contains("NEWER CodeCompass"); i++) Thread.Sleep(100);
            Assert.DoesNotContain("NEWER CodeCompass", r);
            for (int i = 0; i < 50 && !ServerContext.IsLinkedOwnerForTest(lib.Root); i++) { CodeCompassTools.SearchCode("ProjectProbe"); Thread.Sleep(100); }
            Assert.True(ServerContext.IsLinkedOwnerForTest(lib.Root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(project.Root);
        }
    }

    // A long wait (for a terminal build) can pile up a whole tree of captured edits. Past a limit they become one rebuild -
    // gated like any other, so disclosed on a share - instead of a huge apply under the write lock.
    [Fact]
    public void Server_TooManyEditsBeforeTheIndexLoads_BecomeARebuild()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false"); // as on a share: no in-session rebuild
            var many = Enumerable.Range(0, 50_100).Select(i => repo.FullPath($"gen/f{i}.txt")).ToList();
            ServerContext.OnChangesForTest(new ChangeBatch(many, FullReconcile: false));

            string r = "";
            for (int i = 0; i < 100 && !(r = CodeCompassTools.SearchCode("AlphaLocal")).Contains("results may be STALE"); i++) Thread.Sleep(100);
            Assert.Contains("results may be STALE", r);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // ...but an ordinary big change (a 2,500-file `git pull` before the first query, or during a build) is just applied: a
    // low limit turned it into a second full build, or, where in-session rebuilds are gated off, dropped it with a note.
    [Fact]
    public void Server_ThousandsOfEditsBeforeTheIndexLoads_AreApplied()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false"); // as on a share: no in-session rebuild
            repo.Write("b.cs", "namespace N { class PulledProbe { } }");
            var pulled = Enumerable.Range(0, 2499).Select(i => repo.FullPath($"gone/f{i}.cs")).Append(repo.FullPath("b.cs")).ToList();
            ServerContext.OnChangesForTest(new ChangeBatch(pulled, FullReconcile: false));

            string r = "";
            for (int i = 0; i < 100 && !(r = CodeCompassTools.SearchCode("PulledProbe")).Contains("b.cs"); i++) Thread.Sleep(100);
            Assert.Contains("b.cs", r);
            Assert.DoesNotContain("results may be STALE", r);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A retry of held-back edits (the index still unreadable) mustn't throw away a C# model built since the hold began: each
    // retry, every few seconds, cost find_references a full rebuild.
    [Fact]
    public void Server_ARetryOfHeldBackEdits_KeepsAModelBuiltSince()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { public class AlphaLocal { public void Ping() { } } }");
        repo.Write("u.cs", "namespace N { class U { void M(AlphaLocal a) { a.Ping(); } } }");
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            using (var wl = IndexWriteLock.Acquire(IndexStore.CacheDirPath(repo.Root))) { wl.SourcesUnchanged(); wl.Commit(); } // another writer
            repo.Write("c.cs", "namespace N { class C { void M(AlphaLocal a) { a.Ping(); } } }");
            var symbolsManifest = Path.Combine(IndexStore.CacheDirPath(repo.Root), "symbols.manifest");
            using (new FileStream(symbolsManifest, FileMode.Open, FileAccess.Read, FileShare.None)) // unreadable throughout
            {
                ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("c.cs") }, FullReconcile: false)); // held; model dropped
                Assert.Contains("c.cs", CodeCompassTools.FindReferences("Ping")); // a model built from the files as they are now
                Thread.Sleep(2500);                                                 // a retry or two hold the same edit again
                Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "a retry must not drop the model built since");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A linked root's full rebuild after lost events that FAILS (a full disk, say): anything may have changed in it, so the C#
    // model that read it goes - it stayed, answering from the old sources.
    [Fact]
    public void Server_AFailedLinkedRebuild_DropsTheCSharpModel()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "class P { void Use() { new Lib().Go(); } }\n");
        lib.Write("lib.cs", "public class Lib { public void Go() { } }\n");
        var (t0, s0, _) = RepositoryIndexer.Build(lib.Root);
        t0.Dispose(); s0.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        var otherSession = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(lib.Root)); // no watcher here
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(project.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "true"); // a local linked root: the rebuild runs
            CodeCompassTools.Reindex();
            Assert.Contains("p.cs", CodeCompassTools.FindReferences("Go"));
            RepositoryIndexer.OnFileStartForTests = _ => throw new IOException("simulated: the disk is full");
            ServerContext.OnLinkedChangesForTest(lib.Root, new ChangeBatch(Array.Empty<string>(), FullReconcile: true));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "after lost events the model may be stale");
        }
        finally
        {
            RepositoryIndexer.OnFileStartForTests = null;
            otherSession?.Dispose();
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(project.Root);
        }
    }

    // The watcher starts with the session, but the index loads on the first query: an edit before then was dropped. On a
    // network or large repo no startup stat-walk catches it either, so it stayed missing until a terminal `update`.
    [Fact]
    public void Server_AnEditBeforeTheFirstQuery_IsNotLost()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false"); // no startup stat-walk, as on a share
            repo.Write("b.cs", "namespace N { class EarlyEditProbe { } }");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("b.cs") }, FullReconcile: false));

            string r = "";
            for (int i = 0; i < 100 && !(r = CodeCompassTools.SearchCode("EarlyEditProbe")).Contains("b.cs"); i++) Thread.Sleep(100);
            Assert.Contains("b.cs", r);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(repo.Root);
        }
    }

    // A linked root's update that failed (its index briefly unreadable) lost its edits: nothing retried them, and the file
    // stayed missing from that root's index until it changed again.
    [Fact]
    public void Server_ALinkedRootEditThatCouldntBeWritten_IsKept_AndRetried()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "namespace P { class ProjectProbe { } }");
        lib.Write("l.cs", "namespace L { class LinkedEarlyProbe { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(lib.Root);
        t0.Dispose(); s0.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        var otherSession = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(lib.Root)); // no watcher here: only the retry can land it
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(project.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            Assert.Contains("LinkedEarlyProbe", CodeCompassTools.SearchCode("LinkedEarlyProbe"));

            lib.Write("late.cs", "namespace L { class LinkedLateProbe { } }");
            var libSymbols = Path.Combine(IndexStore.CacheDirPath(lib.Root), "symbols.manifest");
            using (new FileStream(libSymbols, FileMode.Open, FileAccess.Read, FileShare.None)) // its index can't be loaded now
            {
                ServerContext.OnLinkedChangesForTest(lib.Root, new ChangeBatch(new[] { lib.FullPath("late.cs") }, FullReconcile: false));
                Assert.Contains("aren't in their index yet", CodeCompassTools.SearchCode("ProjectProbe")); // said, not silent
            }

            bool landed = false;
            for (int i = 0; i < 150 && !landed; i++)
            {
                Thread.Sleep(100);
                if (RepositoryIndexer.TryLoad(lib.Root, out var t, out var s))
                    using (t) using (s) landed = t.Search("LinkedLateProbe").Count > 0;
            }
            Assert.True(landed, "the linked root's held-back edit never landed");
        }
        finally
        {
            otherSession?.Dispose();
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(project.Root);
        }
    }

    // One live watcher per repo: a second session serves read-only (no watcher) and takes over when the first exits.
    [Fact]
    public void Server_SecondSession_ServesReadOnly_ThenTakesOverLiveIndexing()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class RoleProbe { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();

        var otherSession = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(repo.Root));
        Assert.NotNull(otherSession);
        ServerContext.Init(repo.Root);
        try
        {
            Assert.False(ServerContext.IsLiveWatchOwnerForTest);
            ServerContext.EnableLiveIndex();
            Assert.False(ServerContext.HasWatcherForTest);                      // the owner watches, not us
            Assert.Contains("a.cs", CodeCompassTools.SearchCode("RoleProbe")); // ...but we still serve

            otherSession!.Dispose();                                           // the owning session exits
            CodeCompassTools.SearchCode("RoleProbe");                          // next query takes over the role
            Assert.True(ServerContext.IsLiveWatchOwnerForTest);
            Assert.True(ServerContext.HasWatcherForTest);
        }
        finally
        {
            otherSession?.Dispose();
            ServerContext.Init(repo.Root);
        }
    }

    // The same hand-over for a LINKED root: it was only ever owned at link time, so once its owning session exited nobody
    // watched it - this session's copy went silently stale until a terminal `update` or a re-point.
    [Fact]
    public void Server_ReadOnlyLinkedRoot_TakesOverLiveIndexing_WhenItsOwnerExits()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "namespace P { class ProjectProbe { } }");
        lib.Write("l.cs", "namespace L { class LinkedRoleProbe { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(lib.Root);
        t0.Dispose(); s0.Dispose();
        LinkStore.Add(project.Root, lib.Root);

        var otherSession = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(lib.Root));
        Assert.NotNull(otherSession);
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(project.Root);
        try
        {
            // The takeover's reconcile would otherwise run in the background, past this test's end and into the next one.
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            Assert.Contains("LinkedRoleProbe", CodeCompassTools.SearchCode("LinkedRoleProbe")); // served read-only
            Assert.False(ServerContext.IsLinkedOwnerForTest(lib.Root));

            otherSession!.Dispose();                                   // the linked root's owner exits
            CodeCompassTools.SearchCode("LinkedRoleProbe");            // next query takes over the role
            Assert.True(ServerContext.IsLinkedOwnerForTest(lib.Root));
            Assert.True(ServerContext.LinkedHasWatcherForTest(lib.Root));
        }
        finally
        {
            otherSession?.Dispose();
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(project.Root);
        }
    }

    // Taking over a linked root whose folder is gone (deleted, an unplugged drive, a share that's down): its watcher can't
    // start, and that threw out of the query - and left the root owned but unwatched for the rest of the session, which
    // also kept every other session from taking it over. It must keep serving read-only and retry later.
    [Fact]
    public void Server_LinkedRootTakeover_WhenItsFolderIsGone_KeepsServing_AndRetriesLater()
    {
        using var project = new TempRepo();
        var lib = new TempRepo();
        var away = lib.Root + "-away";
        project.Write("p.cs", "namespace P { class ProjectProbe { } }");
        lib.Write("l.cs", "namespace L { class GoneLinkedProbe { } }");
        var (t0, s0, _) = RepositoryIndexer.Build(lib.Root);
        t0.Dispose(); s0.Dispose();
        LinkStore.Add(project.Root, lib.Root);

        var otherSession = WriteOwnership.TryAcquire(IndexStore.CacheDirPath(lib.Root));
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        var oldRetry = ServerContext.LinkedPromoteRetryMs;
        ServerContext.Init(project.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            ServerContext.LinkedPromoteRetryMs = 0; // retry on the very next query
            CodeCompassTools.Reindex();
            Assert.Contains("GoneLinkedProbe", CodeCompassTools.SearchCode("GoneLinkedProbe")); // served read-only

            Directory.Move(lib.Root, away);                                    // the folder goes away...
            otherSession!.Dispose();                                           // ...and its owner exits
            Assert.Contains("GoneLinkedProbe", CodeCompassTools.SearchCode("GoneLinkedProbe")); // no failed call: still served
            Assert.False(ServerContext.IsLinkedOwnerForTest(lib.Root));        // not claimed while it can't be watched

            Directory.Move(away, lib.Root);                                    // it's back
            CodeCompassTools.SearchCode("GoneLinkedProbe");
            Assert.True(ServerContext.IsLinkedOwnerForTest(lib.Root));
            Assert.True(ServerContext.LinkedHasWatcherForTest(lib.Root));
        }
        finally
        {
            otherSession?.Dispose();
            ServerContext.LinkedPromoteRetryMs = oldRetry;
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(project.Root);
            if (Directory.Exists(away) && !Directory.Exists(lib.Root)) Directory.Move(away, lib.Root);
            lib.Dispose();
        }
    }

    // Review P1-6: a watcher overflow on a network/huge root used to start an UNGATED in-session full rebuild (hours
    // over SMB). Now it keeps serving, discloses possible staleness on every query, and clears once a fresh update
    // (here another process's) is installed.
    [Fact]
    public void Server_LostEventsOnNetworkRoot_DisclosesInsteadOfRebuilding_ClearsAfterUpdate()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class LostEventsProbe { } }");
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            ServerContext.OnChangesForTest(new ChangeBatch(Array.Empty<string>(), FullReconcile: true));

            var r = CodeCompassTools.SearchCode("LostEventsProbe");
            Assert.Contains("a.cs", r);                    // still serving (no rebuild took it down)
            Assert.Contains("lost change events", r);      // ...and says results may be stale
            Assert.Contains("codecompass update", r);

            Thread.Sleep(20); // meta timestamps are compared against the loss time
            OtherWriter(repo.Root);
            Assert.DoesNotContain("lost change events", CodeCompassTools.SearchCode("LostEventsProbe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", old);
            ServerContext.Init(repo.Root);
        }
    }

    // Review P2-30: the live-watch role across REAL processes - a running `codecompass watch` holds it (so a session
    // here can't start a second watcher), and killing that process releases it with no stale lock left behind.
    [Fact]
    public async Task LiveWatchRole_HeldByAnotherProcess_ThenFreedWhenItIsKilled()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class WatchRole { } }");
        var cacheDir = IndexStore.GetCacheDir(repo.Root);
        using var p = TestCli.Start(cli, "watch", repo.Root);
        try
        {
            var watching = Task.Run(() =>
            {
                string? line;
                while ((line = p.StandardError.ReadLine()) is not null)
                    if (line.Contains("watching")) return true;
                return false;
            });
            Assert.True(await watching.WaitAsync(TimeSpan.FromSeconds(120)), "the CLI watch never started");
            _ = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();

            Assert.Null(WriteOwnership.TryAcquire(cacheDir)); // held by the live `watch` process
        }
        finally
        {
            p.Kill(entireProcessTree: true); // a crash/kill, not a clean exit
            p.WaitForExit(30_000);
        }

        WriteOwnership? mine = null;
        for (int i = 0; i < 100 && mine is null; i++) { mine = WriteOwnership.TryAcquire(cacheDir); if (mine is null) Thread.Sleep(100); }
        Assert.NotNull(mine); // the OS dropped the dead process's handle - nothing to clean up by hand
        mine!.Dispose();
    }

    // Real cross-process exclusion: a terminal `codecompass index` waits for an in-flight write held by another
    // process (this test host), says so, and completes once it's released - it is never refused.
    [Fact]
    public async Task Cli_Index_WaitsForAnotherProcessesWrite_ThenCompletes()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class CrossProcess { } }");
        var holder = IndexWriteLock.TryAcquire(IndexStore.GetCacheDir(repo.Root))!;
        using var p = TestCli.Start(cli, "index", repo.Root);
        // stderr carries \r-rewritten progress lines first; read until the wait notice (or the CLI exits).
        var sawNotice = Task.Run(() =>
        {
            string? line;
            while ((line = p.StandardError.ReadLine()) is not null)
                if (line.Contains("waiting for another CodeCompass process")) return true;
            return false;
        });
        try
        {
            Assert.True(await sawNotice.WaitAsync(TimeSpan.FromSeconds(60)), "the CLI never reported waiting for the lock");
            Assert.False(p.HasExited); // blocked, not failed
        }
        finally { holder.Dispose(); }

        _ = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000));
        Assert.Equal(0, p.ExitCode);
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
        using (t) using (s) Assert.NotEmpty(t.Search("CrossProcess"));
    }
}
