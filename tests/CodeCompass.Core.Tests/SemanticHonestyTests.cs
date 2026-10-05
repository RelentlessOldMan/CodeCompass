using System;
using System.IO;
using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Mcp;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// Review WP-C (C/C++ honesty + determinism) and WP-D (focus correctness + federation honesty). Each test pins a case
// where the tool used to return a confident-but-wrong answer: a silently "complete" pass that skipped files, a result
// set that varied between identical runs, or a focus/federation note that misdescribed what was searched.
public class SemanticHonestyTests
{
    private static string BigCpp(string symbolUse)
    {
        var sb = new StringBuilder();
        sb.AppendLine("void widget_reset();");
        for (int i = 0; i < 40_000; i++) sb.AppendLine("// filler line to push this translation unit over the size cap");
        sb.AppendLine("void big_caller() { " + symbolUse + " }");
        return sb.ToString();
    }

    // P0-4: a candidate .cpp over CODECOMPASS_CPP_MAX_TU_MB was filtered out BEFORE the candidate count, so the pass
    // read as complete, the lexical backfill skipped every C/C++ file, and the big file's references vanished silently.
    [Fact]
    public void OversizeCppCandidate_MakesThePassIncomplete_AndIsDisclosed_AndBackfilled()
    {
        using var repo = new TempRepo();
        repo.Write("small.cpp", "void widget_reset();\nvoid caller() { widget_reset(); }\n");
        repo.Write("big.cpp", BigCpp("widget_reset();"));
        Assert.True(new FileInfo(repo.FullPath("big.cpp")).Length > 1024 * 1024);
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_MAX_TU_MB");
        ServerContext.Init(repo.Root);
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_TU_MB", "1");
            var direct = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("widget_reset",
                new[] { repo.FullPath("small.cpp"), repo.FullPath("big.cpp") });
            Assert.Equal(1, direct.SkippedTooBig);
            Assert.True(SemanticCoverage.IsCppPassIncomplete(direct.MemoryStopped, direct.ParsedTus, direct.CandidateTus,
                direct.UnresolvedIncludes.Count, direct.SkippedTooBig, direct.WorkerFailed));

            CodeCompassTools.Reindex();
            var r = CodeCompassTools.FindReferences("widget_reset");
            Assert.Contains("big.cpp", r);            // the backfill found the reference clang never parsed
            Assert.Contains("size cap", r);           // ...and the answer says why it's lexical
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_TU_MB", old);
            ServerContext.Init(repo.Root);
        }
    }

    // P1-9: TUs parse in parallel and merge in completion order, USRs sit in a hash set - so the truncated SET varied
    // between identical runs. Results are now canonical (path, line, column) before the cut.
    [Fact]
    public void CppReferences_AreCanonicallyOrdered_SoTruncationIsDeterministic()
    {
        using var repo = new TempRepo();
        foreach (var n in new[] { "c", "a", "b" })
            repo.Write($"{n}.cpp", "void probe_fn();\nvoid f_" + n + "() { probe_fn(); probe_fn(); }\n");
        var files = new[] { "c", "a", "b" }.Select(n => repo.FullPath($"{n}.cpp")).ToList();
        var an = new ClangCppAnalyzer(repo.Root);
        var full = an.FindReferencesDetailed("probe_fn", files).Locations;
        var keys = full.Select(l => (l.RelativePath, l.Line, l.Column)).ToList();
        Assert.Equal(keys.OrderBy(k => k.RelativePath, StringComparer.Ordinal).ThenBy(k => k.Line).ThenBy(k => k.Column), keys);
        var capped = an.FindReferencesDetailed("probe_fn", files, max: 3).Locations;
        Assert.Equal(keys.Take(3), capped.Select(l => (l.RelativePath, l.Line, l.Column)));
    }

    // P2-1: libclang columns are UTF-8 BYTES; the lexical layer counts UTF-16 chars. A non-ASCII prefix made the same
    // reference two different "file:line:col" keys, so it was listed twice.
    [Theory]
    [InlineData("foo();", 1, 1)]
    [InlineData("/* é */ foo();", 10, 9)]       // é is 2 UTF-8 bytes
    [InlineData("/* 中 */ foo();", 11, 9)]       // 中 is 3 UTF-8 bytes
    [InlineData("/* \U0001F600 */ foo();", 12, 10)]  // an emoji: 4 bytes, 2 UTF-16 chars
    public void ClangByteColumns_AreConvertedToCharColumns(string line, int byteCol, int charCol) =>
        Assert.Equal(charCol, ClangCppAnalyzer.CharColumn(line, byteCol));

    // P2-7: a worker crash was reported as a MEMORY stop (pointing at the wrong knob); only an OOM signature is.
    [Fact]
    public void ContainedWorkerFailure_IsLabeledAsAWorkerFailure_UnlessItIsAnOom()
    {
        var crash = ClangSubprocess.ContainedFailure(new[] { "a.cpp" }, "access violation");
        Assert.True(crash.WorkerFailed);
        Assert.False(crash.MemoryStopped);
        var oom = ClangSubprocess.ContainedFailure(new[] { "a.cpp" }, "LLVM ERROR: out of memory");
        Assert.True(oom.MemoryStopped);
        Assert.False(oom.WorkerFailed);
        var none = ClangSubprocess.ContainedFailure(null, null); // still incomplete -> backfill runs
        Assert.True(SemanticCoverage.IsCppPassIncomplete(none.MemoryStopped, none.ParsedTus, none.CandidateTus, 0, 0, none.WorkerFailed));
        Assert.Contains(ReferenceMerge.CppCoverageBits(0, 1, false, Array.Empty<string>(), workerFailed: true), b => b.Contains("worker crashed"));
    }

    [Fact]
    public void Worker_RejectsMalformedRequest_WithTheRetryableExitCode()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{ not json"));
        using var output = new MemoryStream();
        Assert.Equal(ClangSubprocess.BadRequestExit, ClangSubprocess.RunWorkerCore(input, output));
    }

    // P1-10: Roslyn's reference enumeration order isn't contractual; the answer is now canonical before the cap.
    [Fact]
    public void CSharpReferences_AreCanonicallyOrdered()
    {
        using var repo = new TempRepo();
        repo.Write("z/Def.cs", "namespace N { public static class T { public static void Ping() { } } }");
        foreach (var n in new[] { "c", "a", "b" })
            repo.Write($"{n}.cs", "namespace N { class U" + n + " { void M() { T.Ping(); T.Ping(); } } }");
        var refs = new RoslynCSharpAnalyzer(repo.Root).FindReferences("Ping");
        var keys = refs.Select(r => (r.RelativePath, r.Line, r.Column)).ToList();
        Assert.Equal(6, keys.Count);
        Assert.Equal(keys.OrderBy(k => k.RelativePath, StringComparer.Ordinal).ThenBy(k => k.Line).ThenBy(k => k.Column), keys);
    }

    // P2-3: a .cs file the semantic model couldn't read (an editor's exclusive lock) was silently absent: zero refs
    // from it, no backfill, no disclosure.
    [Fact]
    public void UnreadableCSharpFile_IsDisclosed_NotSilentlyCovered()
    {
        using var repo = new TempRepo();
        repo.Write("Def.cs", "namespace N { public static class T { public static void Ping() { } } }");
        repo.Write("Locked.cs", "namespace N { class L { void M() { T.Ping(); } } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            ServerContext.EvictSemanticAnalyzersNow(); // force the next query to rebuild the model while the file is locked
            using (new FileStream(repo.FullPath("Locked.cs"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var r = CodeCompassTools.FindReferences("Ping");
                Assert.Contains("could not be read", r);
                Assert.Contains("Locked.cs", r);
            }
        }
        finally { ServerContext.Init(repo.Root); }
    }

    private static (TempRepo Project, TempRepo External) Federation(string projectFile, string projectText, string extFile, string extText)
    {
        var project = new TempRepo();
        var external = new TempRepo();
        project.Write(projectFile, projectText);
        external.Write(extFile, extText);
        var (et, es, _) = RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        LinkStore.Add(project.Root, external.Root);
        return (project, external);
    }

    // P0-6a: the analyzer truncated at the probe BEFORE the focus filter, so a focused root's references could be
    // crowded out by out-of-scope ones - silently, with no "MORE EXIST" and no backfill.
    [Fact]
    public void Focus_InScopeReferences_AreNotStarvedByOutOfScopeOnes()
    {
        var calls = string.Concat(Enumerable.Range(0, 40).Select(i => $"void M{i}() {{ T.Ping(); }} "));
        var (project, external) = Federation(
            "src/P.cs", "namespace N { public static class T { public static void Ping() { } } class PUse { " + calls + "} }",
            "lib/E.cs", "namespace N { class EUse { void A() { T.Ping(); } void B() { T.Ping(); } void C() { T.Ping(); } } }");
        using (project) using (external)
        {
            ServerContext.Init(project.Root);
            try
            {
                CodeCompassTools.Reindex();
                CodeCompassTools.ManageLinks("focus", external.Root);
                var r = CodeCompassTools.FindReferences("Ping", maxResults: 5);
                Assert.Equal(3, r.Split('\n').Count(l => l.Contains("E.cs:")));  // all three in-scope references
                Assert.DoesNotContain("P.cs:", r);
            }
            finally { ServerContext.Init(project.Root); }
        }
    }

    // P0-6b: C/C++ semantic hits bypassed the focus filter. Candidates only come from the focused root, so the leak is
    // a HEADER that lives in an out-of-scope root: clang (resolving across all roots, as it must) reports the reference
    // inside it, and that hit was displayed although its root was focused out.
    [Fact]
    public void Focus_FiltersCppSemanticHits_ByOwningRoot()
    {
        var (project, external) = Federation(
            "shared.h", "void shared_fn();\ninline void in_header() { shared_fn(); }\n",
            "e.cpp", "#include \"shared.h\"\nvoid e_caller() { shared_fn(); }\n");
        using (project) using (external)
        {
            ServerContext.Init(project.Root);
            try
            {
                CodeCompassTools.Reindex();
                Assert.Contains("shared.h", CodeCompassTools.FindReferences("shared_fn")); // unscoped: both roots
                CodeCompassTools.ManageLinks("focus", external.Root);
                var r = CodeCompassTools.FindReferences("shared_fn");
                Assert.Contains("e.cpp", r);
                Assert.DoesNotContain("shared.h", r);   // its root is out of scope
            }
            finally { ServerContext.Init(project.Root); }
        }
    }

    // P0-6c: focusing a linked-but-unindexed root alongside an indexed one claimed both were searched.
    [Fact]
    public void Focus_OnAnUnindexedRoot_WarnsAtFocusTime_AndOnEveryScopedResult()
    {
        using var project = new TempRepo();
        using var built = new TempRepo();
        using var unbuilt = new TempRepo();
        project.Write("a.cs", "class ProjectThing { }");
        built.Write("b.cs", "class BuiltThing { }");
        unbuilt.Write("c.cs", "class UnbuiltThing { }");
        var (t, s, _) = RepositoryIndexer.Build(built.Root);
        t.Dispose(); s.Dispose();
        LinkStore.Add(project.Root, built.Root);
        LinkStore.Add(project.Root, unbuilt.Root);
        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            var reply = CodeCompassTools.ManageLinks("focus", built.Root + "," + unbuilt.Root);
            Assert.Contains("WARNING", reply);
            Assert.Contains(unbuilt.Root, reply);
            var r = CodeCompassTools.SearchCode("BuiltThing");
            Assert.Contains("b.cs", r);
            Assert.Contains("no index yet and were NOT searched", r);
        }
        finally { ServerContext.Init(project.Root); }
    }

    // P0-5: the "files over the size cap" honesty note read only the PRIMARY root's meta - blind to a linked root's
    // gaps, and (under focus) reporting an excluded root's gaps instead.
    [Fact]
    public void CoverageCaveat_CountsTheRootsActuallySearched()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("a.cs", "class A { }");
        external.Write("small.cs", "class S { }");
        external.Write("huge.txt", new string('x', 2 * 1024 * 1024)); // over a 1 MB cap -> excluded from the index
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", "1");
            var (t, s, _) = RepositoryIndexer.Build(external.Root);
            t.Dispose(); s.Dispose();
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", old); }
        Assert.True(IndexMetaFile.Read(external.Root)!.FilesOverCap > 0);
        LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("exceed the size cap", CodeCompassTools.SearchCode("NothingMatchesThisToken"));
            CodeCompassTools.ManageLinks("focus", project.Root); // the gap is in a root this search no longer covers
            Assert.DoesNotContain("exceed the size cap", CodeCompassTools.SearchCode("NothingMatchesThisToken"));
        }
        finally { ServerContext.Init(project.Root); }
    }

    // A focus names the PREVIOUS project's roots; a re-point must start unscoped (ServerContext.Init clears it).
    [Fact]
    public void Focus_IsClearedByARepoint()
    {
        var (project, external) = Federation("a.cs", "class ProjA { }", "b.cs", "class ExtB { }");
        using var other = new TempRepo();
        other.Write("o.cs", "class OtherThing { }");
        using (project) using (external)
        {
            ServerContext.Init(project.Root);
            try
            {
                CodeCompassTools.Reindex();
                CodeCompassTools.ManageLinks("focus", external.Root);
                ServerContext.Init(other.Root);
                CodeCompassTools.Reindex();
                Assert.DoesNotContain("Focus is set", CodeCompassTools.ManageLinks("list"));
                Assert.Empty(ServerContext.CurrentFocus);
                var r = CodeCompassTools.SearchCode("OtherThing");
                Assert.Contains("o.cs", r);
                Assert.DoesNotContain("Scoped to", r);
            }
            finally { ServerContext.Init(project.Root); }
        }
    }

    // P2-28: index staleness was disclosed only on ZERO results; a stale index under-reporting a NON-empty answer
    // was silent in MCP (the CLI already warned on every query).
    [Fact]
    public void StaleIndex_IsDisclosedOnNonEmptyResultsToo()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class StaleProbe { }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            var metaPath = Path.Combine(IndexStore.GetCacheDir(repo.Root), "meta.json");
            var meta = System.Text.Json.JsonSerializer.Deserialize<IndexMeta>(File.ReadAllText(metaPath))!;
            File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { ContentVersion = 0 }));
            var r = CodeCompassTools.SearchCode("StaleProbe");
            Assert.Contains("a.cs", r);
            Assert.Contains("older indexer", r);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // Review P1-7: the MCP request's own token reaches the semantic passes - an abandoned/cancelled call stops instead
    // of burning a full Roslyn build or clang parse under the read lock, and says what happened.
    [Fact]
    public void CancelledRequest_StopsTheSemanticPass_AndSaysSo()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class C { void Ping() { } void M() { Ping(); } } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            ServerContext.EvictSemanticAnalyzersNow(); // the next query must build the model - and the request is cancelled
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            Assert.Equal("The request was cancelled.", CodeCompassTools.FindReferences("Ping", cancellationToken: cts.Token));
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("Ping")); // an uncancelled call still works
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void FocusNote_UsesFullPaths_WhenTwoRootsShareAFolderName()
    {
        var a = Path.Combine(Path.GetTempPath(), "x1", "src");
        var b = Path.Combine(Path.GetTempPath(), "x2", "src");
        var names = ServerContext.RootNames(new[] { a, b });
        Assert.Contains("x1", names);
        Assert.Contains("x2", names);
        Assert.Equal("one, two", ServerContext.RootNames(new[] { Path.Combine(Path.GetTempPath(), "one"), Path.Combine(Path.GetTempPath(), "two") }));
    }
}
