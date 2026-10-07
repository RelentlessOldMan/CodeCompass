using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Field reports these pin (via RepositoryIndexer's test seams, which hold or fail ONE file mid-build):
//  - the stall watchdog fires and names the file wedging a worker (it once stayed silent for whole builds);
//  - on a network share it says "likely slow transfer", not "exclude it" (wrong advice for a slow share);
//  - a per-file OutOfMemoryException is not fatal: the build completes, the file is disclosed as NOT
//    INDEXED, and the next update picks it up.
[Collection("compaction-env")] // serialize the CODECOMPASS_FORCE_NETWORK env mutation and the static seams
public class IndexFaultInjectionTests
{
    private static string RepoLog(string root)
    {
        var key = IndexStore.RepoKey(Path.GetFullPath(root));
        var file = Directory.GetFiles(Log.Directory, $"repo-*-{key}.log").Single();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd();
    }

    // Runs a full build with the seams set, capturing stderr; always clears the seams.
    private static string BuildCapturingStderr(string root, Action<string> onFileStart, long? stallMs = null)
    {
        var prevErr = Console.Error;
        var err = new StringWriter();
        RepositoryIndexer.OnFileStartForTests = onFileStart;
        RepositoryIndexer.StallWarnMsForTests = stallMs;
        try
        {
            Console.SetError(err);
            var (t, s, _) = RepositoryIndexer.Build(root);
            t.Dispose(); s.Dispose();
        }
        finally
        {
            Console.SetError(prevErr);
            RepositoryIndexer.OnFileStartForTests = null;
            RepositoryIndexer.StallWarnMsForTests = null;
        }
        return err.ToString();
    }

    private static TempRepo RepoWithSlowFile()
    {
        var repo = new TempRepo();
        for (int i = 0; i < 8; i++) repo.Write($"src/ok{i}.cs", $"class Ok{i} {{ }}");
        repo.Write("src/wedged.cs", "class Wedged { }");
        return repo;
    }

    private static void HoldWedged(string rel)
    {
        if (rel.EndsWith("wedged.cs", StringComparison.Ordinal)) System.Threading.Thread.Sleep(1500);
    }

    [Fact]
    public void Watchdog_WorkerHeldOnOneFile_FiresAndNamesTheFile()
    {
        using var repo = RepoWithSlowFile();

        var stderr = BuildCapturingStderr(repo.Root, HoldWedged, stallMs: 300);

        Assert.Contains("indexing slow:", stderr);
        Assert.Contains("slow-to-parse file", stderr);              // local wording
        Assert.Contains("stuck on: src/wedged.cs", stderr.Replace('\\', '/'));
        var log = RepoLog(repo.Root).Replace('\\', '/');
        Assert.Contains("src/wedged.cs", log);                      // the full in-flight list is logged too
        Assert.Contains("exclude it (CODECOMPASS_IGNORE)", log);
    }

    [Fact]
    public void Watchdog_OnNetworkRoot_SaysSlowTransfer()
    {
        using var repo = RepoWithSlowFile();
        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        string stderr;
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            stderr = BuildCapturingStderr(repo.Root, HoldWedged, stallMs: 300);
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev); }

        Assert.Contains("over a network share - likely slow transfer, not a stuck build", stderr);
        Assert.DoesNotContain("slow-to-parse", stderr);
    }

    [Fact]
    public void Watchdog_FastBuild_StaysQuiet()
    {
        using var repo = RepoWithSlowFile();
        var stderr = BuildCapturingStderr(repo.Root, _ => { }, stallMs: 300);
        Assert.DoesNotContain("indexing slow", stderr);
        Assert.DoesNotContain("made no progress", stderr);
    }

    [Fact]
    public void StallReport_AdviceDependsOnNetworkAndProgress()
    {
        // Local: name the file and advise excluding it / lowering the caps.
        var local = RepositoryIndexer.StallReport(stuck: 1, onNetwork: false, hardStall: false, 60, 10, 0);
        Assert.Contains("slow-to-parse", local.Headline);
        Assert.StartsWith("If a file has been held", local.Advice);

        // Share, bytes still flowing: slow transfer that will finish - don't tell them to exclude it.
        var share = RepositoryIndexer.StallReport(stuck: 1, onNetwork: true, hardStall: false, 60, 10, 0);
        Assert.Contains("likely slow transfer, not a stuck build", share.Headline);
        Assert.StartsWith("Over a network share this is normal", share.Advice);
        Assert.Contains("only if the build never completes", share.Advice);

        // Share, but nothing moved at all: that's a real stall, so the culprit advice applies again.
        var shareStalled = RepositoryIndexer.StallReport(stuck: 1, onNetwork: true, hardStall: true, 60, 10, 0);
        Assert.StartsWith("If a file has been held", shareStalled.Advice);

        // No single file held: a whole-build stall.
        var none = RepositoryIndexer.StallReport(stuck: 0, onNetwork: false, hardStall: true, 60, 10, 0);
        Assert.StartsWith("indexing made no progress for ~60s", none.Headline);
    }

    [Fact]
    public void Build_OneFileOutOfMemory_CompletesDisclosesItAndUpdatePicksItUp()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 5; i++) repo.Write($"src/ok{i}.cs", $"class Ok{i} {{ }} // marker_ok_{i}");
        repo.Write("src/victim.cs", "class Victim { } // marker_victim_zq");

        var stderr = BuildCapturingStderr(repo.Root, rel =>
        {
            if (rel.EndsWith("victim.cs", StringComparison.Ordinal)) throw new OutOfMemoryException();
        });

        // The build completed with every other file, and said loudly that one is missing.
        Assert.Contains("1 file(s) were SKIPPED under memory pressure and are NOT in this index", stderr);
        Assert.Contains("NOT INDEXED - skipped under memory pressure", RepoLog(repo.Root));
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t0, out var s0));
        using (t0) using (s0)
        {
            Assert.Equal(5, t0.DocumentCount);
            Assert.Empty(t0.Search("marker_victim_zq", 100));
            Assert.Single(t0.Search("marker_ok_3", 100));
        }

        // Memory freed: a plain update finds the file missing from the snapshot and indexes it.
        var (t1, s1, stats) = RepositoryIndexer.Update(repo.Root);
        using (t1) using (s1)
        {
            Assert.Equal(1, stats.Added);
            Assert.Equal(6, t1.DocumentCount);
            Assert.Single(t1.Search("marker_victim_zq", 100));
            Assert.NotEmpty(s1.FindByName("Victim"));
        }
    }
}
