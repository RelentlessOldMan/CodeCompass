using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// All MCP tool tests live in one class so they share ServerContext's static state
// serially (xunit runs methods within a class sequentially). The collection serializes this
// against CompactionTests too - both drive the process-global CODECOMPASS_COMPACT_SEGMENTS env.
[Collection("compaction-env")]
public class McpToolsTests
{
    private static TempRepo NewIndexedRepo()
    {
        var repo = new TempRepo();
        repo.Write("src/Widget.cs", """
        namespace App
        {
            public class Widget
            {
                public void Run() { Helper(); }
                private void Helper() { }
            }
        }
        """);
        repo.Write("src/Caller.cs", """
        namespace App
        {
            public class Caller
            {
                public void Go()
                {
                    var w = new Widget();
                    w.Run();                     // call site
                    // remember to Run it again
                    var label = "Run";
                }
            }
        }
        """);
        repo.Write("src/calc.cpp", """
        int square(int x) { return x * x; }
        int useit() { return square(3); }
        """);
        repo.Write("docs/notes.md", "Run appears in prose here.");
        ServerContext.Init(repo.Root);
        CodeCompassTools.Reindex();
        return repo;
    }

    [Fact]
    public void SearchCode_FindsMatches()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.SearchCode("Helper");
        Assert.Contains("src/Widget.cs", result);
        Assert.Contains("Helper", result);
    }

    [Fact]
    public void SearchCode_SignalsTruncationVsExactCount()
    {
        using var repo = new TempRepo();
        // 10 lines all containing FOObar; searching with a cap of 3 must flag that more exist,
        // while a cap that covers everything must NOT claim truncation.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 10; i++) sb.Append("int FOObar").Append(i).Append(" = ").Append(i).Append(";\n");
        repo.Write("regs.cs", sb.ToString());
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            var capped = CodeCompassTools.SearchCode("FOObar", maxResults: 3);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(capped, "regs.cs:").Count); // only 3 shown
            Assert.Contains("MORE EXIST", capped);                                                   // truncation signalled

            var full = CodeCompassTools.SearchCode("FOObar", maxResults: 50);
            Assert.DoesNotContain("MORE EXIST", full); // all 10 fit -> exact count, no truncation
            Assert.Contains("(10 matches)", full);
        }
        finally { ServerContext.Init(repo.Root); } // reset shared static state
    }

    [Fact]
    public void FindDefinition_ReturnsSymbolLocation()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.FindDefinition("Widget");
        Assert.Contains("Class Widget", result);
        Assert.Contains("src/Widget.cs", result);
    }

    [Fact]
    public void FindDefinition_UnknownName_ReportsNothing()
    {
        using var repo = NewIndexedRepo();
        Assert.Contains("No definition", CodeCompassTools.FindDefinition("Nonexistent"));
    }

    [Fact]
    public void SearchSymbols_SubstringCaseInsensitive()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.SearchSymbols("widget");
        Assert.Contains("Widget", result);
    }

    [Fact]
    public void FindReferences_SemanticForCSharp_LexicalForOthers()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.FindReferences("Run");

        // Semantic C#: the real call site is found...
        Assert.Contains("src/Caller.cs", result);
        Assert.Contains("w.Run()", result);
        Assert.Contains("1 C# +", result);
        // ...but the comment and the "Run" string in the .cs file are NOT counted.
        Assert.DoesNotContain("remember to Run", result);
        Assert.DoesNotContain("var label", result);
        // Lexical fallback still covers non-semantic files (the markdown prose).
        Assert.Contains("docs/notes.md", result);
    }

    [Fact]
    public void FindReferences_SemanticForCpp()
    {
        using var repo = NewIndexedRepo();
        var result = CodeCompassTools.FindReferences("square");
        Assert.Contains("src/calc.cpp", result);
        Assert.Contains("square(3)", result);
    }

    [Fact]
    public void FindReferences_SignalsTruncationExactlyAtCap()
    {
        using var repo = new TempRepo();
        // 6 non-semantic (.py) files each referencing `handler` as a whole word -> lexical refs.
        for (int i = 0; i < 6; i++) repo.Write($"m{i}.py", "def go():\n    handler()\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            // Cap below the true count -> must signal truncation with the shared 'MORE EXIST' sentinel.
            var capped = CodeCompassTools.FindReferences("handler", maxResults: 3);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(capped, "\\.py:").Count);
            Assert.Contains("MORE EXIST", capped);

            // Cap above the true count -> exact, no false 'MAY EXIST' (the old threshold bug).
            var full = CodeCompassTools.FindReferences("handler", maxResults: 50);
            Assert.DoesNotContain("MORE EXIST", full);
            Assert.DoesNotContain("MAY EXIST", full);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void StartupReconcile_PicksUpOutOfSessionChange()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex(); // build the index; a.cs recorded in the snapshot

            // An external change with NO watcher running (as if edited/synced while Claude was closed).
            repo.Write("b.cs", "class BrandNewExternalType { }\n");

            // New "session": re-init drops the in-memory index; the first tool call loads it and kicks
            // the background startup reconcile (local + tiny => gated on).
            ServerContext.Init(repo.Root);

            bool found = false;
            for (int i = 0; i < 50 && !found; i++)
            {
                if (CodeCompassTools.SearchCode("BrandNewExternalType").Contains("b.cs")) found = true;
                else Thread.Sleep(100);
            }
            Assert.True(found, "startup reconcile should pick up the externally-added file");
        }
        finally { ServerContext.Init(repo.Root); } // reset shared static state
    }

    [Fact]
    public void PublishesStatusFile_ReadableByStatuslineCommand()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex(); // Swap -> PublishStatus (offloaded write)

            // The write is best-effort/async; poll briefly for the "ready" status the server publishes.
            CodeCompass.Core.Storage.IndexStatus? status = null;
            for (int i = 0; i < 50 && status?.State != "ready"; i++)
            {
                status = CodeCompass.Core.Storage.IndexStatusFile.Read(repo.Root);
                if (status?.State != "ready") Thread.Sleep(50);
            }
            Assert.NotNull(status);
            Assert.Equal("ready", status!.State);
            Assert.Equal(1, status.Files);            // the one file we indexed
            Assert.Contains("file", status.Text);     // "1 files"
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void StatusFileRead_ForUnindexedRepo_IsNullAndCreatesNoCacheDir()
    {
        using var repo = new TempRepo();
        var cacheDir = CodeCompass.Core.Storage.IndexStore.CacheDirPath(repo.Root);
        // A repo we've never indexed has no status; reading it must not create the cache dir
        // (the status line runs on every render, for every folder Claude visits).
        Assert.Null(CodeCompass.Core.Storage.IndexStatusFile.Read(repo.Root));
        Assert.False(System.IO.Directory.Exists(cacheDir), "read-only status probe must not create the cache dir");
    }

    [Fact]
    public void ConcurrentSearchAndReindex_DoesNotCrash()
    {
        using var repo = NewIndexedRepo();
        int stop = 0;
        Exception? failure = null;

        var searchers = Enumerable.Range(0, 4).Select(threadNo => new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    // Result may be search hits or a status string mid-rebuild; both are fine.
                    _ = CodeCompassTools.SearchCode("Run");
                    _ = CodeCompassTools.FindDefinition("Widget");
                }
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
        })).ToList();

        searchers.ForEach(t => t.Start());
        for (int i = 0; i < 10; i++) CodeCompassTools.Reindex(); // disposes + swaps the mmap index under readers
        Volatile.Write(ref stop, 1);
        searchers.ForEach(t => t.Join());

        Assert.Null(failure); // no ObjectDisposed/AccessViolation from searching a swapped index
    }

    [Fact]
    public void Soak_ConcurrentSearchReindexWatchedEditsAndCompaction_NoCrash()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS");
        Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", "3"); // force frequent compaction
        using var repo = new TempRepo();
        try
        {
            for (int i = 0; i < 5; i++) repo.Write($"f{i}.cs", $"namespace N {{ class C{i} {{ void M(){{}} }} }}");
            ServerContext.Init(repo.Root);
            CodeCompassTools.Reindex();          // become Ready
            ServerContext.EnableLiveIndex(100);  // live watcher -> incremental + compaction under edits

            int stop = 0;
            Exception? failure = null;
            void Guard(Action loop) { try { loop(); } catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); } }

            var threads = new System.Collections.Generic.List<Thread>();
            for (int t = 0; t < 3; t++)
                threads.Add(new Thread(() => Guard(() =>
                {
                    while (Volatile.Read(ref stop) == 0) { _ = CodeCompassTools.SearchCode("class"); _ = CodeCompassTools.FindDefinition("C1"); }
                })));
            threads.Add(new Thread(() => Guard(() =>
            {
                while (Volatile.Read(ref stop) == 0) { CodeCompassTools.Reindex(); Thread.Sleep(150); }
            })));
            threads.Add(new Thread(() => Guard(() =>
            {
                int i = 0;
                while (Volatile.Read(ref stop) == 0)
                {
                    // The indexer may have the file open for reading; a real editor writes
                    // atomically, so tolerate our own naive-write contention here.
                    try { repo.Write($"f{i % 5}.cs", $"namespace N {{ class C{i % 5} {{ void M{i}(){{}} }} }}"); }
                    catch (System.IO.IOException) { }
                    i++;
                    Thread.Sleep(40);
                }
            })));

            threads.ForEach(t => t.Start());
            Thread.Sleep(4000);
            Volatile.Write(ref stop, 1);
            threads.ForEach(t => t.Join());
            ServerContext.StopLiveIndex();

            Assert.Null(failure); // no crash from search racing reindex/compaction/watcher edits
            // Still functional afterward.
            CodeCompassTools.Reindex();
            Assert.Contains("class", CodeCompassTools.SearchCode("class"));
        }
        finally
        {
            ServerContext.StopLiveIndex();
            Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", old);
            ServerContext.Init(repo.Root); // reset shared static state for other tests
        }
    }

    [Fact]
    public void LargeWorkspace_PuntsToCliInsteadOfIndexing()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");
        Environment.SetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB", "0"); // any repo "too large"
        try
        {
            ServerContext.Init(repo.Root);
            var ready = ServerContext.TryGet(out _, out _, out var status);
            Assert.False(ready);
            Assert.Contains("codecompass index", status);
            // The tool relays that message rather than hanging.
            Assert.Contains("codecompass index", CodeCompassTools.SearchCode("A"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB", null);
            ServerContext.Init(repo.Root);
        }
    }

    [Fact]
    public void SmallWorkspace_BackgroundIndexes_ThenServes()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Widget { } }");
        ServerContext.Init(repo.Root);

        bool ready = false;
        string status = "";
        for (int i = 0; i < 200 && !ready; i++)
        {
            ready = ServerContext.TryGet(out _, out _, out status);
            if (!ready) Thread.Sleep(25);
        }

        Assert.True(ready, $"index never became ready; last status: {status}");
        Assert.Contains("Widget", CodeCompassTools.FindDefinition("Widget"));
    }
}
