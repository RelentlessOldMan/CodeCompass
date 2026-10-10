using System;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Config;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Mcp;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// A process has ONE ambient repo config (CodeCompassConfig.Current), and every index write loads its root's: in a server
// that also writes linked roots, it's whichever root was written last. Code that walked or pruned the PROJECT with it
// used another root's ignore/keepDirs - the startup prune deleted the project's keepDirs files from the index, the C#
// analyzer walked the wrong folders, watchers dropped the wrong events. Each must use its own root's config.
public class AmbientConfigTests
{
    // The root cause: the ambient config was one process-wide value that every Load switched - a linked root's write on a
    // watcher thread changed it under a query or the project's own build. A Load now applies to the loading flow only.
    [Fact]
    public void ALoadOnAnotherThread_DoesNotSwitchThisThreadsConfig()
    {
        using var mine = new TempRepo();
        using var theirs = new TempRepo();
        mine.Write(".codecompass.json", "{ \"keepDirs\": [\"packages\"] }");
        try
        {
            CodeCompassConfig.Load(mine.Root);
            var other = new System.Threading.Thread(() => CodeCompassConfig.Load(theirs.Root)); // a linked root's write
            other.Start(); other.Join();
            Assert.Contains("packages", CodeCompassConfig.KeptDirs());
        }
        finally { CodeCompassConfig.Load(theirs.Root); }
    }

    // ...over a process default - the server's project - that a thread which loaded nothing (a timer, a watcher callback)
    // sees.
    [Fact]
    public void AThreadThatLoadedNothing_SeesTheProcessDefault()
    {
        using var project = new TempRepo();
        using var other = new TempRepo();
        project.Write(".codecompass.json", "{ \"keepDirs\": [\"packages\"] }");
        try
        {
            CodeCompassConfig.LoadDefault(project.Root);
            bool seen = false;
            using (System.Threading.ExecutionContext.SuppressFlow()) // nothing of this thread's flows into it
            {
                var t = new System.Threading.Thread(() => seen = System.Linq.Enumerable.Contains(CodeCompassConfig.KeptDirs(), "packages"));
                t.Start(); t.Join();
            }
            Assert.True(seen);
        }
        finally { CodeCompassConfig.LoadDefault(other.Root); }
    }

    [Fact]
    public void PruneIgnored_UsesTheRootsOwnConfig_NotTheAmbientOne()
    {
        using var project = new TempRepo();
        using var other = new TempRepo();
        project.Write(".codecompass.json", "{ \"keepDirs\": [\"packages\"] }");
        project.Write("packages/lib/Kept.cs", "class KeptProbe { }\n");
        var (t0, s0, _) = RepositoryIndexer.Build(project.Root);
        t0.Dispose(); s0.Dispose();
        try
        {
            CodeCompassConfig.Load(other.Root); // another root's config is ambient: its rules skip packages/
            var u = RepositoryIndexer.PruneIgnored(project.Root);
            u.Text?.Dispose(); u.Symbols?.Dispose();
            Assert.Equal(0, u.Pruned);
            Assert.True(RepositoryIndexer.TryLoad(project.Root, out var t, out var s));
            using (t) using (s) Assert.NotEmpty(t.Search("KeptProbe"));
        }
        finally { CodeCompassConfig.Load(other.Root); }
    }

    [Fact]
    public void Analyzer_WalksEachRootWithItsOwnConfig()
    {
        using var project = new TempRepo();
        using var other = new TempRepo();
        project.Write(".codecompass.json", "{ \"keepDirs\": [\"packages\"] }");
        project.Write("packages/lib/A.cs", "public class A { public void Ping() { } }\n");
        project.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        try
        {
            CodeCompassConfig.Load(other.Root); // as a linked-root write leaves it
            using var analyzer = new RoslynCSharpAnalyzer(project.Root);
            Assert.Contains(analyzer.FindReferences("Ping"), r => r.RelativePath == "b.cs"); // A (under packages/) resolved
        }
        finally { CodeCompassConfig.Load(other.Root); }
    }

    // A watcher's lost events on a root that turned auto-reconcile off must not start an in-session rebuild because another
    // root's config happened to be ambient.
    [Fact]
    public void LostEvents_FollowTheProjectsOwnAutoReconcileSetting()
    {
        using var project = new TempRepo();
        using var other = new TempRepo();
        project.Write(".codecompass.json", "{ \"autoReconcile\": false }");
        project.Write("a.cs", "class AmbientProbe { }\n");
        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            CodeCompassConfig.Load(other.Root); // as a linked-root write leaves it (no autoReconcile setting: a local repo would rebuild)
            ServerContext.OnChangesForTest(new ChangeBatch(Array.Empty<string>(), FullReconcile: true));
            Assert.Contains("lost change events", CodeCompassTools.SearchCode("AmbientProbe")); // not rebuilt: disclosed instead
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Watchers_FilterEventsByTheirOwnRootsRules()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "class P { }\n");
        lib.Write(".codecompass.json", "{ \"keepDirs\": [\"packages\"] }");
        lib.Write("packages/x/L.cs", "class LinkedKeptProbe { }\n");
        var (t0, s0, _) = RepositoryIndexer.Build(lib.Root);
        t0.Dispose(); s0.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        var oldAuto = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        ServerContext.Init(project.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false");
            CodeCompassTools.Reindex();
            Assert.Contains("LinkedKeptProbe", CodeCompassTools.SearchCode("LinkedKeptProbe")); // the linked root is loaded, owned, watched
            Assert.False(ServerContext.LinkedWatcherIgnoresDirForTest(lib.Root, "packages")); // its own rules keep packages/

            CodeCompassConfig.Load(lib.Root); // as a linked-root write leaves it
            ServerContext.EnableLiveIndex();
            Assert.True(ServerContext.WatcherIgnoresDirForTest("packages")); // the project's rules skip packages/
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", oldAuto);
            ServerContext.Init(project.Root);
        }
    }
}
