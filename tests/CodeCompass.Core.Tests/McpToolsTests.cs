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

    // Guardrail (v1.0.201): a whitespace-only query has no trigrams and would fall to a full-corpus scan
    // returning noise; it must be rejected like an empty one.
    [Fact]
    public void SearchCode_WhitespaceOnly_IsRejected()
    {
        using var repo = NewIndexedRepo();
        Assert.Contains("non-empty", CodeCompassTools.SearchCode("   "));
    }

    // Guardrail (v1.0.201): a relative link path would resolve against the server's launch dir, not the
    // workspace - reject it so the linked root is deterministic.
    [Fact]
    public void ManageLinks_Add_RelativePath_IsRejected()
    {
        using var repo = NewIndexedRepo();
        var msg = CodeCompassTools.ManageLinks("add", "../sibling");
        Assert.Contains("ABSOLUTE", msg);
    }

    // The symmetric guard: `remove` must reject a relative path for the same reason `add` does (it would
    // resolve against the server's launch dir, not the workspace).
    [Fact]
    public void ManageLinks_Remove_RelativePath_IsRejected()
    {
        using var repo = NewIndexedRepo();
        var msg = CodeCompassTools.ManageLinks("remove", "..\\sibling");
        Assert.Contains("ABSOLUTE", msg);
    }

    // Review finding: `remove purge=true` on a linked root this session has searched was always refused - the server itself
    // held the index open, so the all-or-nothing clear said "in use, close it and retry", which the agent can't do from
    // inside its own session. The server lets go of the unlinked root first, so the purge deletes it.
    [Fact]
    public void ManageLinks_RemovePurge_OfALinkedRootThisSessionLoaded_DeletesIt()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class A { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class PurgeMeGizmo { } }");
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);
        var cacheDir = CodeCompass.Core.Storage.IndexStore.CacheDirPath(external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("Gizmo.cs", CodeCompassTools.SearchCode("PurgeMeGizmo")); // loaded: the server holds it open

            var msg = CodeCompassTools.ManageLinks("remove", external.Root, purge: true);
            Assert.Contains("index deleted", msg);
            Assert.False(System.IO.Directory.Exists(cacheDir));
            Assert.DoesNotContain("Gizmo.cs", CodeCompassTools.SearchCode("PurgeMeGizmo")); // no longer federated
        }
        finally
        {
            ServerContext.Init(project.Root);
            CodeCompass.Core.Storage.LinkStore.Remove(project.Root, external.Root);
            CodeCompass.Core.Storage.IndexStore.TryClearCacheDir(cacheDir, out _);
        }
    }

    // An unrecognized action must return the usage default, not silently behave like "list" or throw.
    [Fact]
    public void ManageLinks_UnknownAction_ReturnsUsage()
    {
        using var repo = NewIndexedRepo();
        var msg = CodeCompassTools.ManageLinks("bogus");
        Assert.Contains("Unknown action", msg);
        Assert.Contains("bogus", msg);
    }

    // Guardrail (v1.0.201): reindex must not run a synchronous rebuild of an over-limit workspace (it would
    // block/timeout the MCP call and orphan the build) - it returns the CLI guidance instead.
    [Fact]
    public void Reindex_OverAutoLimit_ReturnsCliGuidance()
    {
        using var repo = new TempRepo();
        repo.Write("src/A.cs", "namespace N { class A { } }");
        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB", "0"); // every workspace is now over the limit
            ServerContext.Init(repo.Root);
            var msg = CodeCompassTools.Reindex();
            Assert.Contains("codecompass index", msg); // guidance, not a "Reindexed N files" completion
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_AUTO_MB", prev);
            ServerContext.Init(repo.Root);
        }
    }

    [Fact]
    public void FindReferences_ExcludesDataAndDocFileMatches()
    {
        // Regression for the QSPR benchmark: a symbol name appearing in a CSV export or a tool's JSON
        // tag dump was reported as a "reference", drowning the real hits. Those data files are not code.
        using var repo = new TempRepo();
        repo.Write("src/Widget.cs", "namespace App { public class Widget { public void Run() { } } }");
        repo.Write("src/Caller.cs", "namespace App { public class Caller { public void Go() { new Widget().Run(); } } }");
        repo.Write("exports/tickets.csv", "id,summary\nQPR-1,Widget is slow on Run\n");
        repo.Write("exports/tags.json", "[{\"name\":\"Widget\"},{\"name\":\"Widget\"}]");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            var refs = CodeCompassTools.FindReferences("Widget");
            Assert.Contains("src/Caller.cs", refs);      // the real code usage IS a reference
            Assert.DoesNotContain("tickets.csv", refs);  // a CSV row is NOT
            Assert.DoesNotContain("tags.json", refs);    // a JSON tag dump is NOT
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void FindDefinition_EmptyResult_DisclosesSymbolSkippedFiles()
    {
        using var repo = new TempRepo();
        // A code file over the symbol cap (1 MB default): fully text-searchable, but tree-sitter symbol
        // extraction is skipped - so its definitions are invisible to find_definition/search_symbols.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("namespace Gen { public class NeedleType { public void Poke() { } } }");
        while (sb.Length < 1_100_000) sb.AppendLine("// filler comment line to push this file over the symbol cap");
        repo.Write("gen/Big.cs", sb.ToString());
        repo.Write("app/Small.cs", "namespace App { public class Small { } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            // The build must have recorded the symbol-skip coverage gap.
            var meta = CodeCompass.Core.Storage.IndexMetaFile.Read(repo.Root);
            Assert.NotNull(meta);
            Assert.True(meta!.FilesSymbolSkipped >= 1, "the over-cap code file should be counted as symbol-skipped");

            // find_definition finds nothing (symbols were skipped) but must NOT read as "doesn't exist":
            // it discloses the symbol-skip gap and points at the text search that CAN find it.
            var def = CodeCompassTools.FindDefinition("NeedleType");
            Assert.StartsWith("No definition found", def);
            Assert.Contains("NO symbols extracted", def);
            Assert.Contains("search_code", def);

            // Positive control: the definition really is there, reachable by text search.
            Assert.Contains("gen/Big.cs", CodeCompassTools.SearchCode("NeedleType"));

            // A small indexed file's symbols are still found normally (no false gap for the common case).
            Assert.Contains("app/Small.cs", CodeCompassTools.FindDefinition("Small"));
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void SemanticAnalyzers_EvictThenRebuild_ReturnSameResults()
    {
        using var repo = NewIndexedRepo();
        try
        {
            // First find_references builds and caches the (large) semantic analyzer.
            var first = CodeCompassTools.FindReferences("Run");
            Assert.Contains("src/Caller.cs", first);
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "a semantic query should make the analyzer resident");

            // Idle-eviction (what the background timer does after CODECOMPASS_SEMANTIC_IDLE_MIN) frees it.
            ServerContext.EvictSemanticAnalyzersNow();
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "eviction must drop the resident analyzer");

            // The next query must rebuild lazily and return byte-identical results.
            var second = CodeCompassTools.FindReferences("Run");
            Assert.Equal(first, second);
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "reuse after eviction should rebuild it");
        }
        finally { ServerContext.Init(repo.Root); } // reset shared static state
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

            // The boundary the field review flagged: a cap EXACTLY equal to the count is complete, not truncated.
            var exact = CodeCompassTools.SearchCode("FOObar", maxResults: 10);
            Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(exact, "regs.cs:").Count);
            Assert.DoesNotContain("MORE EXIST", exact);
            Assert.Contains("(10 matches)", exact);
        }
        finally { ServerContext.Init(repo.Root); } // reset shared static state
    }

    // QSPR field report: a JIRA CSV row was counted as a reference, so find_references now reads code files only. That
    // exclusion must not leak into search_code - literal search is exactly how you find a name in a CSV, JSON or log.
    [Fact]
    public void SearchCode_StillFindsDataFiles_ThatFindReferencesSkips()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.c", "int main(void){ return reset_vector_zq(); }\n");
        repo.Write("docs/issues.csv", "id,summary\nPROJ-1,reset_vector_zq crashes on boot\n");
        repo.Write("cfg/map.json", "{ \"entry\": \"reset_vector_zq\" }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            var search = CodeCompassTools.SearchCode("reset_vector_zq");
            Assert.Contains("docs/issues.csv", search);
            Assert.Contains("cfg/map.json", search);
            Assert.Contains("src/app.c", search);

            var refs = CodeCompassTools.FindReferences("reset_vector_zq");
            Assert.Contains("src/app.c", refs);
            Assert.DoesNotContain("issues.csv", refs);
            Assert.DoesNotContain("map.json", refs);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void SearchCode_CaseInsensitive_FindsAllCases_ButCaseSensitiveDoesNot()
    {
        using var repo = new TempRepo();
        repo.Write("mix.cs", "var Handler = 1;\nvar handler = 2;\nvar HANDLER = 3;\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            // Case-sensitive (default): only the exact-case line.
            var cs = CodeCompassTools.SearchCode("handler", caseSensitive: true);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, "mix.cs:"));
            Assert.Contains("var handler = 2", cs);

            // Case-insensitive: all three casings.
            var ci = CodeCompassTools.SearchCode("handler", caseSensitive: false);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(ci, "mix.cs:").Count);
            Assert.Contains("Handler", ci);
            Assert.Contains("HANDLER", ci);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void Tools_EmptyOrWhitespaceInput_ReturnGuidance_NotCrash()
    {
        using var repo = NewIndexedRepo();
        // An agent can pass an empty/blank argument; every tool must return a short "provide ..." hint
        // rather than throw or run a degenerate query.
        Assert.Contains("non-empty", CodeCompassTools.SearchCode(""));
        Assert.Contains("Provide", CodeCompassTools.FindDefinition("   "));
        Assert.Contains("Provide", CodeCompassTools.SearchSymbols(""));
        Assert.Contains("Provide", CodeCompassTools.FindReferences("  "));
        Assert.Contains("Provide", CodeCompassTools.FindCallees(""));
    }

    [Fact]
    public void Tools_NonPositiveMaxResults_AreClamped_NotCrash()
    {
        using var repo = NewIndexedRepo();
        // A 0 / negative maxResults (agent-supplied) must be clamped to a sane floor (1), not throw or
        // silently return "nothing" or a "provide ..." guidance string. "Run" really exists, so a clamped
        // query still returns a hit (and, since more exist, flags truncation rather than dropping them).
        var zero = CodeCompassTools.SearchCode("Run", maxResults: 0);
        Assert.Contains("Run", zero);
        Assert.Contains("MORE EXIST", zero);            // clamped to 1 of several -> truncation signalled
        Assert.DoesNotContain("Provide", zero);
        Assert.Contains("Run", CodeCompassTools.SearchCode("Run", maxResults: -5));
        Assert.Contains("Run", CodeCompassTools.FindReferences("Run", maxResults: -1));
        Assert.Contains("Widget", CodeCompassTools.SearchSymbols("Widget", maxResults: 0));
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
    public void EmptyResults_DiscloseCoverageGap_WhenFilesExceedTheCap()
    {
        using var repo = new TempRepo();
        repo.Write("small.cs", "class A { }\n");
        repo.Write("big.cs", new string('x', 1_200 * 1024));   // 1.2 MB
        repo.Write(".codecompass.json", "{ \"maxFileMb\": 1 }"); // repo-local cap => big.cs (1.2 MB) is excluded
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            // A term absent from the indexed content: the zero must DISCLOSE that a file was excluded by
            // the cap (honest zero - a match could be in it), not just say "nothing".
            var res = CodeCompassTools.SearchCode("definitely_absent_token_zzq");
            Assert.Contains("No matches", res);
            Assert.Contains("exceed the size cap", res);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void EmptyResults_IncludeActionableNextStepHints()
    {
        using var repo = NewIndexedRepo();
        // Each dead-end should route the agent to a sensible next tool/option instead of just "nothing".
        Assert.Contains("caseSensitive:false", CodeCompassTools.SearchCode("NoSuchTextZZZ")); // default is case-sensitive
        Assert.Contains("search_symbols", CodeCompassTools.FindDefinition("NoSuchSymbolZZZ"));
        Assert.Contains("search_code", CodeCompassTools.SearchSymbols("NoSuchSymbolZZZ"));
        Assert.Contains("search_code", CodeCompassTools.FindReferences("NoSuchSymbolZZZ"));
    }

    [Fact]
    public void FindDefinition_ReportsLineRange_AndInlinesSmallDefinition()
    {
        using var repo = NewIndexedRepo();
        // Helper is a one-liner method inside Widget; there's exactly one definition, so find_definition
        // reports a start-end range and inlines the source (saving the agent a follow-up file read).
        var result = CodeCompassTools.FindDefinition("Helper");
        Assert.Matches(@"src/Widget\.cs:\d+(-\d+)?:\d+: Method Helper", result); // range form
        Assert.Contains("private void Helper()", result);                        // inlined source line
        Assert.Contains(": ", result);                                           // "<n>: <code>" snippet prefix
    }

    [Fact]
    public void FindDefinition_MultiLineClass_HasEndLineBeyondStart()
    {
        using var repo = NewIndexedRepo();
        // Widget is a multi-line class -> its end line must be past its start (a real range, not a point).
        var result = CodeCompassTools.FindDefinition("Widget");
        var m = System.Text.RegularExpressions.Regex.Match(result, @"src/Widget\.cs:(\d+)-(\d+):");
        Assert.True(m.Success, $"expected a start-end range for the class; got:\n{result}");
        Assert.True(int.Parse(m.Groups[2].Value) > int.Parse(m.Groups[1].Value), "class end line must exceed start");
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
        Assert.Contains("1 C# semantic +", result);
        // ...but the comment and the "Run" string in the .cs file are NOT counted.
        Assert.DoesNotContain("remember to Run", result);
        Assert.DoesNotContain("var label", result);
        // And a mention in prose/doc (.md) is NOT a code reference - find_references is about code
        // usages, not every place the string appears. (Lexical fallback for non-semantic *code* files
        // is covered by FindReferences_SignalsTruncationExactlyAtCap, which uses .py.)
        Assert.DoesNotContain("docs/notes.md", result);
    }

    [Fact]
    public void FindCallees_ListsInRepoCallTargets_ForCSharp()
    {
        using var repo = NewIndexedRepo(); // Widget.Run() calls Helper()
        var result = CodeCompassTools.FindCallees("Run");
        Assert.Contains("src/Widget.cs", result); // Helper is defined there
        Assert.Contains("Helper", result);
    }

    [Fact]
    public void FindCallees_DistinguishesUnknownSymbolFromNoCallees()
    {
        // The silent-empty hazard the benchmark flagged: a bare "nothing" for both an unknown symbol and
        // a real method that calls no repo code turns a typo into a false finding. They must read differently.
        using var repo = new TempRepo();
        repo.Write("src/Leaf.cs", "namespace App { public class Leaf { public void Ping() { System.Console.WriteLine(\"hi\"); } } }");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            // Known method that only calls the framework -> "defined here, but calls no in-repo methods".
            var known = CodeCompassTools.FindCallees("Ping");
            Assert.Contains("defined here", known);

            // A name that doesn't exist -> "no symbol named" (NOT the same message).
            var unknown = CodeCompassTools.FindCallees("Nonexistent");
            Assert.Contains("No symbol named", unknown);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void Federation_SearchesLinkedRoots_WithAbsolutePaths()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { public void Run() { } } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class Gizmo { public void UniqueExternalThing() { } } }");

        // Build the external root's own index, then link it to the project (as `link add` would).
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex(); // builds the project + loads the linked index

            // A symbol that exists ONLY in the linked root is found, shown as an ABSOLUTE path.
            var ext = CodeCompassTools.SearchCode("UniqueExternalThing");
            Assert.Contains("UniqueExternalThing", ext);
            Assert.Contains(external.Root, ext);           // absolute path into the linked root
            var def = CodeCompassTools.FindDefinition("Gizmo");
            Assert.Contains(external.Root, def);
            Assert.Contains("Gizmo", def);

            // A project hit stays repo-relative (compact), not absolute.
            var proj = CodeCompassTools.SearchCode("Widget");
            Assert.Contains("src/App.cs", proj);
            Assert.DoesNotContain(external.Root, proj);
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Federation_OwnedLinkedRoot_PicksUpLiveEdits()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { public void Run() { } } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class Gizmo { } }");

        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            // First federated query loads the linked root and (nothing else holds it) wins write-ownership,
            // so this session live-watches the linked root - exactly like the project root. (A "no matches"
            // reply echoes the query, so key the assertions on the FILE PATH, which only a real hit carries.)
            Assert.DoesNotContain("Fresh.cs", CodeCompassTools.SearchCode("BrandNewLinkedSymbol"));

            // Edit the linked root out-of-band; the owner's watcher should index it live and federate it.
            external.Write("lib/Fresh.cs", "namespace Ext { public class Fresh { public void BrandNewLinkedSymbol() { } } }");

            string res = "";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < System.TimeSpan.FromSeconds(20))
            {
                res = CodeCompassTools.SearchCode("BrandNewLinkedSymbol");
                if (res.Contains("Fresh.cs")) break;
                Thread.Sleep(200);
            }
            Assert.Contains("BrandNewLinkedSymbol", res);
            Assert.Contains("Fresh.cs", res);
            Assert.Contains(external.Root, res); // shown as an absolute path into the linked root
        }
        finally { ServerContext.Init(project.Root); } // resets shared static state (disposes linked watcher)
    }

    [Fact]
    public void Federation_PicksUpLinkAddAndRemove_MidSession()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class Gizmo { public void UniqueMidSessionThing() { } } }");
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        // NOT linked yet - the session starts with no linked roots.

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.DoesNotContain("Gizmo.cs", CodeCompassTools.SearchCode("UniqueMidSessionThing"));

            // Link it in a "terminal" while the session is live - the next query must pick it up, no restart.
            CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);
            var added = CodeCompassTools.SearchCode("UniqueMidSessionThing");
            Assert.Contains("Gizmo.cs", added);
            Assert.Contains(external.Root, added); // linked hit shown absolute

            // Unlink it mid-session - the next query must drop it again.
            CodeCompass.Core.Storage.LinkStore.Remove(project.Root, external.Root);
            Assert.DoesNotContain("Gizmo.cs", CodeCompassTools.SearchCode("UniqueMidSessionThing"));
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Federation_MidSessionLinkAdd_PreservesExistingRootWatcher()
    {
        using var project = new TempRepo();
        using var rootA = new TempRepo();
        using var rootB = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { } }");
        rootA.Write("a/Ay.cs", "namespace A { public class Ay { } }");
        rootB.Write("b/Bee.cs", "namespace B { public class Bee { public void BeeSymbol() { } } }");
        foreach (var r in new[] { rootA.Root, rootB.Root })
        {
            var (t, s, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(r);
            t.Dispose(); s.Dispose();
        }
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, rootA.Root); // A linked from the start

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            _ = CodeCompassTools.SearchCode("Ay"); // load + own + watch A

            // Add B mid-session; the reconcile must KEEP A (its ownership + watcher intact), not rebuild the set.
            CodeCompass.Core.Storage.LinkStore.Add(project.Root, rootB.Root);
            Assert.Contains("Bee.cs", CodeCompassTools.SearchCode("BeeSymbol")); // B picked up live

            // Now edit A. If A's watcher survived the reconcile, this new symbol shows up without a restart.
            rootA.Write("a/Later.cs", "namespace A { public class Later { public void LaterAySymbol() { } } }");
            string res = "";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < System.TimeSpan.FromSeconds(20))
            {
                res = CodeCompassTools.SearchCode("LaterAySymbol");
                if (res.Contains("Later.cs")) break;
                Thread.Sleep(200);
            }
            Assert.Contains("Later.cs", res);          // A's watcher still live after B was added
            Assert.Contains(rootA.Root, res);
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Federation_DuplicateLinkEntries_FederateOnce()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class DupCheckGizmo { } }");
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();

        // A hand-edited / doubly-written links.json listing the same root twice must not federate it twice.
        var cacheDir = CodeCompass.Core.Storage.IndexStore.CacheDirPath(project.Root);
        System.IO.Directory.CreateDirectory(cacheDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(cacheDir, "links.json"),
            System.Text.Json.JsonSerializer.Serialize(new[] { external.Root, external.Root }));

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            var def = CodeCompassTools.FindDefinition("DupCheckGizmo");
            Assert.Contains("DupCheckGizmo", def);
            // Deduped => exactly one definition (a duplicate root would yield "(2 definitions)").
            Assert.DoesNotContain("2 definitions", def);
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Federation_ReindexReprobes_LinkedRootIndexedMidSession()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class Gizmo { public void LateIndexedThing() { } } }");
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root); // linked but NOT yet indexed

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            // Unindexed linked root: TryLoad fails, so it isn't federated yet.
            Assert.DoesNotContain("Gizmo.cs", CodeCompassTools.SearchCode("LateIndexedThing"));

            // Build its index out-of-band (as `codecompass index "<root>"` would for a large deferred root).
            var (t, s, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
            t.Dispose(); s.Dispose();
            // links.json didn't change, so the signature watch alone won't pick it up.
            Assert.DoesNotContain("Gizmo.cs", CodeCompassTools.SearchCode("LateIndexedThing"));

            // reindex is the lever that forces a re-probe -> the now-built linked index gets federated.
            CodeCompassTools.Reindex();
            var res = CodeCompassTools.SearchCode("LateIndexedThing");
            Assert.Contains("Gizmo.cs", res);
            Assert.Contains(external.Root, res);
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Federation_NonOwnerLinkedRoot_ServesReadOnly()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class Gizmo { public void ReadOnlyFederatedThing() { } } }");
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        // Simulate ANOTHER live session already owning the linked root's index: hold its write-ownership so
        // this session's TryAcquire fails and it must serve the root read-only (still federated).
        var otherOwner = CodeCompass.Core.Storage.WriteOwnership.TryAcquire(
            CodeCompass.Core.Storage.IndexStore.CacheDirPath(external.Root));
        Assert.NotNull(otherOwner);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            var res = CodeCompassTools.SearchCode("ReadOnlyFederatedThing");
            Assert.Contains("Gizmo.cs", res);       // federated even though we don't own it
            Assert.Contains(external.Root, res);
        }
        finally
        {
            ServerContext.Init(project.Root);
            otherOwner!.Dispose();
        }
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
    public void FindReferences_CppRepo_SaysMatchedByName_AndSkipsTheDefinition()
    {
        using var repo = new TempRepo();
        repo.Write("main.c", "int helper(void){return 1;}\nint main(void){return helper();}");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            // C/C++ references are a name search: the answer says so plainly, lists the call, and not the definition.
            var result = CodeCompassTools.FindReferences("helper");
            Assert.Contains("matched by NAME", result);
            Assert.Contains("main.c:2:", result);
            Assert.DoesNotContain("main.c:1:", result);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // Field report (1.0.189, two linked repos): a C#-only symbol's answer carried the C/C++ caveat from the OTHER root - the
    // caveat was scoped to the project, not the query. It must appear only when C/C++ files are part of THIS answer.
    [Fact]
    public void FindReferences_CSharpOnlySymbol_CarriesNoCppNote_EvenWithACppLinkedRoot()
    {
        using var project = new TempRepo();
        project.Write("src/App.cs", "namespace N { public static class App { public static int OnlyCsZq() => 1; public static int Use() => OnlyCsZq(); } }\n");
        project.Write("native/util.c", "int util(void){ return 0; }\n");          // C in this root too, not naming the symbol
        using var external = new TempRepo();
        external.Write("lib/drv.c", "int drv(void){ return 2; }\n");              // a C/C++ linked root
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root); et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            var result = CodeCompassTools.FindReferences("OnlyCsZq");
            Assert.Contains("src/App.cs", result);
            Assert.DoesNotContain("matched by NAME", result);
            Assert.DoesNotContain("C/C++", result);
        }
        finally
        {
            CodeCompass.Core.Storage.LinkStore.Remove(project.Root, external.Root);
            ServerContext.Init(project.Root);
        }
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

            // Cap EXACTLY the true count - the reported off-by-one: a `>= maxResults` threshold claimed more existed when
            // the count landed on the limit. All six are shown and nothing is claimed beyond them.
            var exact = CodeCompassTools.FindReferences("handler", maxResults: 6);
            Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(exact, "\\.py:").Count);
            Assert.DoesNotContain("MORE EXIST", exact);
            Assert.DoesNotContain("MAY EXIST", exact);
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

    // The startup reconcile finishes while the session's first find_references is building the C# analyzer. Swapping in
    // an index that found NO changes used to discard that analyzer anyway, so the second C# query rebuilt it (EF Core:
    // 15 s, then 0.6 s from the third call). An unchanged tree must keep it; a real external change must still drop it.
    [Fact]
    public void StartupReconcile_KeepsCSharpAnalyzer_OnlyWhenNothingChanged()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false"); // the test runs the reconcile itself
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() { } void N() { M(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            ServerContext.Init(repo.Root); // new session over the existing index

            Assert.Contains("a.cs", CodeCompassTools.FindReferences("M"));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers());

            ServerContext.RunStartupReconcileNow(); // nothing changed on disk
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "an unchanged reconcile must keep the C# analyzer");

            repo.Write("b.cs", "class B { void P() { new A(); } }\n"); // an out-of-session change
            ServerContext.RunStartupReconcileNow();
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a reconcile that found changes must drop the stale analyzer");
            Assert.Contains("b.cs", CodeCompassTools.FindReferences("A"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", old);
            ServerContext.Init(repo.Root);
        }
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
    public void StartupReconcile_SkippedForNetworkPath()
    {
        // Fake-out: force network treatment on a local temp repo (no real share). A network root must
        // NOT auto-reconcile on startup (slow SMB stat-walk / unreliable watcher -> left to manual update).
        var oldForce = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();

            repo.Write("b.cs", "class BrandNewNetworkType { }\n"); // external change, no watcher
            ServerContext.Init(repo.Root);                          // new "session": would reconcile if local

            // Gated off for network -> the file must not appear on its own within a generous window.
            bool found = false;
            for (int i = 0; i < 15 && !found; i++)
            {
                if (CodeCompassTools.SearchCode("BrandNewNetworkType").Contains("b.cs")) found = true;
                else Thread.Sleep(100);
            }
            Assert.False(found, "a network path must NOT auto-reconcile on startup");

            // Positive control: the change is real - a manual reindex still picks it up.
            CodeCompassTools.Reindex();
            Assert.Contains("b.cs", CodeCompassTools.SearchCode("BrandNewNetworkType"));
        }
        finally
        {
            // Restore, don't null: a suite run deliberately under FORCE_NETWORK=1 must keep it for later tests.
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", oldForce);
            ServerContext.Init(repo.Root); // reset shared static state
        }
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

    // #75: session focus. A wrapper project links several big repos but the user wants to search only one at
    // a time. manage_links action=focus scopes every subsequent search to the chosen root(s); the excluded
    // roots' hits disappear and the result discloses the scoping so a narrowed search isn't read as "absent".
    [Fact]
    public void Focus_ScopesLexicalSearch_ExcludesOtherRoots_AndDiscloses()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class ProjectOnlyWidget { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class ExternalOnlyGizmo { } }");
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            // Unscoped: both roots are searchable, no scoping disclosure.
            Assert.Contains("ProjectOnlyWidget", CodeCompassTools.SearchCode("ProjectOnlyWidget"));
            Assert.Contains("ExternalOnlyGizmo", CodeCompassTools.SearchCode("ExternalOnlyGizmo"));
            Assert.DoesNotContain("Scoped to", CodeCompassTools.SearchCode("ProjectOnlyWidget"));

            // Focus on the linked root (by absolute path): the project (wrapper) root falls out of scope.
            var focusMsg = CodeCompassTools.ManageLinks("focus", external.Root);
            Assert.Contains("Focused on", focusMsg);

            // The project-only symbol is now out of scope: not found, AND the no-match reply discloses the scope.
            var scopedProj = CodeCompassTools.SearchCode("ProjectOnlyWidget");
            Assert.DoesNotContain("src/App.cs", scopedProj);
            Assert.Contains("Scoped to", scopedProj);
            // The linked root stays in scope and is still fully searchable.
            var scopedExt = CodeCompassTools.SearchCode("ExternalOnlyGizmo");
            Assert.Contains("ExternalOnlyGizmo", scopedExt);
            Assert.Contains("Scoped to", scopedExt);

            // Clearing focus restores full federation and removes the disclosure.
            var clearMsg = CodeCompassTools.ManageLinks("focus");
            Assert.Contains("cleared", clearMsg);
            var cleared = CodeCompassTools.SearchCode("ProjectOnlyWidget");
            Assert.Contains("src/App.cs", cleared);
            Assert.DoesNotContain("Scoped to", cleared);
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Focus_ScopesSemanticFindReferences_ByOwningRoot()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        // A type defined in the project is used (new Shared()) in BOTH roots; same namespace, so the
        // all-roots analyzer resolves both. Focus must show only the usages in the focused root.
        project.Write("src/P.cs", "namespace N { public class Shared { } public class PUse { public void M() { var x = new Shared(); } } }");
        external.Write("lib/E.cs", "namespace N { public class EUse { public void M() { var x = new Shared(); } } }");
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            // Unscoped: both usage sites resolve.
            var all = CodeCompassTools.FindReferences("Shared");
            Assert.Contains("P.cs", all);
            Assert.Contains("E.cs", all);

            // Focus the linked root: only its usage survives; the project usage is filtered out.
            CodeCompassTools.ManageLinks("focus", external.Root);
            var ext = CodeCompassTools.FindReferences("Shared");
            Assert.Contains("E.cs", ext);
            Assert.DoesNotContain("P.cs", ext);
            Assert.Contains("Scoped to", ext);

            // Focus the project (by its folder name): now only the project usage survives.
            CodeCompassTools.ManageLinks("focus", System.IO.Path.GetFileName(project.Root));
            var proj = CodeCompassTools.FindReferences("Shared");
            Assert.Contains("P.cs", proj);
            Assert.DoesNotContain("E.cs", proj);
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Focus_UnknownToken_LeavesFocusUnchanged_AndListReportsState()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class Gizmo { } }");
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            // list shows focus off by default.
            Assert.Contains("Focus: off", CodeCompassTools.ManageLinks("list"));

            // A token that matches no root is rejected without changing state.
            var bad = CodeCompassTools.ManageLinks("focus", "no-such-repo");
            Assert.Contains("No root matches", bad);
            Assert.Contains("Widget", CodeCompassTools.SearchCode("Widget")); // still unscoped - project searchable

            // A real focus is reflected by list.
            CodeCompassTools.ManageLinks("focus", external.Root);
            Assert.Contains("Focus: ACTIVE", CodeCompassTools.ManageLinks("list"));
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Focus_SelectingAllRoots_ExcludesNothing_AndCarriesNoScopeNote()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class ProjWidget { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class ExtGizmo { } }");
        var (et, es, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(external.Root);
        et.Dispose(); es.Dispose();
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            // Focusing on EVERY root is a no-op scope: both roots stay searchable and no exclusion is disclosed.
            var msg = CodeCompassTools.ManageLinks("focus", $"{project.Root},{external.Root}");
            Assert.Contains("nothing is excluded", msg);
            var r1 = CodeCompassTools.SearchCode("ProjWidget");
            Assert.Contains("src/App.cs", r1);
            Assert.DoesNotContain("Scoped to", r1);          // focus selected all => no scope note
            Assert.Contains("ExtGizmo", CodeCompassTools.SearchCode("ExtGizmo"));
        }
        finally { ServerContext.Init(project.Root); }
    }

    [Fact]
    public void Focus_OnNotYetIndexedRoot_SaysSo_RatherThanBlackholeTheQuery()
    {
        using var project = new TempRepo();
        using var external = new TempRepo();
        project.Write("src/App.cs", "namespace App { public class Widget { } }");
        external.Write("lib/Gizmo.cs", "namespace Ext { public class Gizmo { } }");
        // Link the external root but DON'T build its index - it's configured (so focus can name it) but not loaded.
        CodeCompass.Core.Storage.LinkStore.Add(project.Root, external.Root);

        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            // Focus resolves against the configured links, so this succeeds...
            Assert.Contains("Focused on", CodeCompassTools.ManageLinks("focus", external.Root));
            // ...but the focused root isn't loaded, so a search says so instead of returning a bare "no match"
            // (which would read as "the symbol doesn't exist").
            var res = CodeCompassTools.SearchCode("Widget");
            Assert.Contains("none of the focused root(s) are indexed", res);
            Assert.DoesNotContain("src/App.cs", res); // the project is out of scope, so its hit must not leak
        }
        finally { ServerContext.Init(project.Root); }
    }
}
