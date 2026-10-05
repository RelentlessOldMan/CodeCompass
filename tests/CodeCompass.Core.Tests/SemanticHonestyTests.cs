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
        for (int i = 0; i < 40_000; i++) sb.AppendLine("// filler line to push this file well past a megabyte");
        sb.AppendLine("void big_caller() { " + symbolUse + " }");
        return sb.ToString();
    }

    // P0-4 (originally a clang size-cap bug: an oversize .cpp's references vanished silently). C/C++ references are a
    // name search now, so a file of any size is searched like any other - and the answer says what C/C++ matches are.
    [Fact]
    public void OversizeCppFile_ReferencesStillFound()
    {
        using var repo = new TempRepo();
        repo.Write("small.cpp", "void widget_reset();\nvoid caller() { widget_reset(); }\n");
        repo.Write("big.cpp", BigCpp("widget_reset();"));
        Assert.True(new FileInfo(repo.FullPath("big.cpp")).Length > 1024 * 1024);
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            var r = CodeCompassTools.FindReferences("widget_reset");
            Assert.Contains("big.cpp", r);
            Assert.Contains("small.cpp", r);
            Assert.Contains("matched by NAME", r);
        }
        finally { ServerContext.Init(repo.Root); }
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

    // P0-6b: C/C++ hits once bypassed the focus filter (a reference inside a HEADER in an out-of-scope root was shown).
    // C/C++ is a name search now; the same guarantee holds: a focused-out root contributes nothing.
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
