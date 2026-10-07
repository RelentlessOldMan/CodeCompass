using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// C/C++ find_references is a NAME search (it replaced the clang pass): every whole-word use in C/C++ code, never a
// comment or string, never the definition site itself, plus one plain note saying what it is. No compiler is
// involved, so a missing header, a huge repo or a broad name can't make it slow, partial or memory-bound.
public class CppNameRefsTests
{
    private static string RunCli(string cli, params string[] args)
    {
        var psi = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "CLI did not exit");
        return o.GetAwaiter().GetResult() + "\n" + e.GetAwaiter().GetResult();
    }

    private static (int Exit, string Output) RunCliWithExit(string cli, params string[] args)
    {
        var psi = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "CLI did not exit");
        p.WaitForExit();
        return (p.ExitCode, o.GetAwaiter().GetResult() + "\n" + e.GetAwaiter().GetResult());
    }

    private static List<string> Lines(string all) => all.Split('\n').Select(l => l.Trim()).ToList();

    [Fact]
    public void Cli_Refs_UsesOnly_NoCommentsStringsOrDefinition()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("include/lib/gear.h", "#pragma once\nint gear_turn(int);\n");
        repo.Write("src/gear.c", "#include \"lib/gear.h\"\nint gear_turn(int x) { return x * 2; }\n");
        repo.Write("src/use.c",
            "#include \"lib/gear.h\"\n" +
            "/* gear_turn is documented here */\n" +
            "int use(void) { return gear_turn(3) + gear_turn(4); }\n" +
            "const char *s = \"gear_turn\";\n");

        RunCli(cli, "index", repo.Root);
        var all = RunCli(cli, "refs", repo.Root, "gear_turn");

        var lines = Lines(all);
        Assert.Equal(2, lines.Count(l => l.StartsWith("src/use.c:3:"))); // both calls
        Assert.DoesNotContain(lines, l => l.StartsWith("src/use.c:2:")); // comment
        Assert.DoesNotContain(lines, l => l.StartsWith("src/use.c:4:")); // string literal
        Assert.DoesNotContain(lines, l => l.StartsWith("src/gear.c:2:")); // the definition is not a use of itself
        Assert.Contains("matched by NAME", all);
    }

    // The old clang pass returned 0 here (the declaration lives in a header that isn't in the tree) and had to fall back
    // to text with a warning. A name search doesn't need the header at all.
    [Fact]
    public void Cli_Refs_MissingHeader_CallsStillFound_NoWarning()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        for (int i = 0; i < 4; i++)
            repo.Write($"mod{i}.c", $"#include \"hwdefs_missing.h\"\nint use_{i}(void){{ return widget_reset({i}); }}\n");

        RunCli(cli, "index", repo.Root);
        var all = RunCli(cli, "refs", repo.Root, "widget_reset");

        Assert.Equal(4, Lines(all).Count(l => l.Contains(".c:2:") && l.Contains("widget_reset(")));
        Assert.DoesNotContain("unresolved", all);
        Assert.Contains("-- 0 C# semantic + 4 name-matched reference(s)", all);
    }

    // C++ members defined in a class body or out of class are definitions, not uses.
    [Fact]
    public void Cli_Refs_CppMemberDefinitions_AreNotUses()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("value.h", "class Value {\npublic:\n  bool isLive() const { return true; }\n  int uses(unsigned n) const;\n};\n");
        repo.Write("value.cpp", "#include \"value.h\"\nint Value::uses(unsigned n) const { return isLive() ? 1 : 0; }\n");
        repo.Write("main.cpp", "#include \"value.h\"\nint main() { Value v; return v.uses(2) + (v.isLive() ? 1 : 0); }\n");

        RunCli(cli, "index", repo.Root);
        var isLive = Lines(RunCli(cli, "refs", repo.Root, "isLive"));
        Assert.DoesNotContain(isLive, l => l.StartsWith("value.h:3:"));      // in-class definition
        Assert.Contains(isLive, l => l.StartsWith("value.cpp:2:"));          // use inside another member
        Assert.Contains(isLive, l => l.StartsWith("main.cpp:2:"));

        var uses = Lines(RunCli(cli, "refs", repo.Root, "uses"));
        Assert.DoesNotContain(uses, l => l.StartsWith("value.cpp:2:"));      // Value::uses definition
        Assert.Contains(uses, l => l.StartsWith("main.cpp:2:"));
    }

    // A file with more uses than the old 16-per-file cap is listed in full (fmt's format.h has 18 of one name).
    [Fact]
    public void Cli_Refs_ManyUsesInOneFile_AllListed()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("busy.cpp", string.Concat(Enumerable.Range(0, 30).Select(i => $"int f{i}() {{ return tick_count(); }}\n")));

        RunCli(cli, "index", repo.Root);
        var all = RunCli(cli, "refs", repo.Root, "tick_count");

        Assert.Equal(30, Lines(all).Count(l => l.StartsWith("busy.cpp:")));
        Assert.DoesNotContain("reached its budget", all);
    }

    // Qualifiers on the answer go to STDOUT with the hits (field report: on stderr, `refs ... > out.txt` silently dropped
    // the C/C++ coverage note). A file past the per-file cap must also say so there - a cut-off list must never read as
    // complete. Checked on the two streams separately; the other CLI tests merge them.
    [Fact]
    public void Cli_Refs_Notes_AreOnStdout()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        int uses = ReferenceMerge.MaxLexicalHitsPerFile + 6;
        repo.Write("busy.c", string.Concat(Enumerable.Range(0, uses).Select(i => $"int f{i}(void) {{ return tick_zq(); }}\n")));

        Assert.Equal(0, TestCli.Run(cli, "index", repo.Root).Exit);
        var (exit, stdout, _) = TestCli.Run(cli, "refs", repo.Root, "tick_zq");

        Assert.Equal(0, exit);
        Assert.Contains("matched by NAME", stdout);
        Assert.Contains("reached its budget", stdout);
        Assert.Equal(ReferenceMerge.MaxLexicalHitsPerFile, Lines(stdout).Count(l => l.StartsWith("busy.c:")));
    }

    // The CLI twin of the MCP check: in a mixed repo, a symbol only C# uses gets no C/C++ caveat (field report: the caveat
    // must follow the query, not the repo's languages).
    [Fact]
    public void Cli_Refs_CSharpOnlySymbol_InAMixedRepo_HasNoCppNote()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("App.cs", "namespace N { public static class App { public static int OnlyCsZq() => 1; public static int Use() => OnlyCsZq(); } }\n");
        repo.Write("util.c", "int util(void){ return 0; }\n");

        Assert.Equal(0, TestCli.Run(cli, "index", repo.Root).Exit);
        var (_, stdout, stderr) = TestCli.Run(cli, "refs", repo.Root, "OnlyCsZq");

        Assert.Contains("App.cs", stdout);
        Assert.DoesNotContain("matched by NAME", stdout + stderr);
    }

    // Build logs and disassembly are not code: they never appear, and never use up the result budget.
    [Fact]
    public void Cli_Refs_NonCodeFiles_Excluded()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("build.log", string.Concat(Enumerable.Range(0, 200).Select(_ => "calling ring_bell()\n")));
        repo.Write("a.c", "int a(void) { return ring_bell(); }\n");

        RunCli(cli, "index", repo.Root);
        var all = RunCli(cli, "refs", repo.Root, "ring_bell");

        Assert.Contains(Lines(all), l => l.StartsWith("a.c:1:"));
        Assert.DoesNotContain("build.log", all);
    }

    // No index (never indexed, or a subfolder of an indexed repo): the name search can't run, so `refs` must not print a
    // confident "0" with exit 0 - it says nothing outside C# was searched and fails like `search`/`def` do (review finding 5).
    [Fact]
    public void Cli_Refs_NoIndex_SaysSoAndFails()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("util.c", "int compute_total(int x) { return x; }\n");
        repo.Write("main.c", "int compute_total(int);\nint main(void) { return compute_total(1) + compute_total(2); }\n");

        var (exit, all) = RunCliWithExit(cli, "refs", repo.Root, "compute_total");

        Assert.NotEqual(0, exit);
        Assert.Contains("no index", all);
        Assert.Contains("were NOT searched", all);
    }

    // A candidate file that can't be read at query time (locked by an editor, an ACL, a share error) used to contribute
    // nothing, silently - a confident 0 if every use was in it. The answer now names it (review finding 6).
    [Fact]
    public void Mcp_UnreadableCandidate_IsDisclosed()
    {
        using var repo = new TempRepo();
        repo.Write("a.c", "int lock_probe_fn(void);\nint a(void) { return lock_probe_fn(); }\n");
        repo.Write("b.c", "int lock_probe_fn(void);\nint b(void) { return lock_probe_fn() + lock_probe_fn(); }\n");
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            string r;
            using (new System.IO.FileStream(repo.FullPath("b.c"), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                r = CodeCompass.Mcp.CodeCompassTools.FindReferences("lock_probe_fn");
            Assert.Contains("a.c:2:", r);
            Assert.Contains("could not be read", r);
            Assert.Contains("b.c", r);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    // A cancelled query (client gave up, session shutting down or re-pointing) must stop the name search between files
    // instead of reading every candidate while holding the index read lock (review finding 11).
    [Fact]
    public void NameSearch_HonorsCancellation()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 5; i++) repo.Write($"f{i}.c", "int cancel_probe_fn(void);\n");
        var (bt, bs, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(repo.Root);
        bt.Dispose(); bs.Dispose();
        Assert.True(CodeCompass.Core.Indexing.RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        using (text) using (symbols)
        {
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                text.Search("cancel_probe_fn", 100, orderByPath: true, accept: _ => true, ct: cts.Token));
        }
    }

    // A qualified C++ query (Widget::spin - a natural spelling for an agent) returned only the out-of-class definition and
    // none of the calls, which spell just `w.spin(...)`. It now searches the member name and says so (review finding 12).
    [Fact]
    public void Mcp_QualifiedCppQuery_FindsTheCalls()
    {
        using var repo = new TempRepo();
        repo.Write("w.cpp", "struct Widget { int spin(int n) const; };\nint Widget::spin(int n) const { return n; }\nint use(Widget w) { return w.spin(3); }\n");
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            var r = CodeCompass.Mcp.CodeCompassTools.FindReferences("Widget::spin");
            Assert.Contains("w.cpp:3:", r);                 // the call
            Assert.DoesNotContain("w.cpp:2:", r);           // not the definition
            Assert.Contains("\"spin\"", r);                 // says which name was matched
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    // Review round 2, finding 8: the split was at the last "::" anywhere, so a qualifier INSIDE template arguments was taken
    // as the member: std::vector<std::string> searched for `string>`. Template arguments are now skipped and dropped (uses
    // like `v.push_back` or `vector<int> v` don't spell them the same way, and a name search needs a plain identifier).
    [Theory]
    [InlineData("Widget::spin", "spin")]
    [InlineData("ns::A::f", "f")]
    [InlineData("::global_fn", "global_fn")]
    [InlineData("std::vector<std::string>", "vector")]
    [InlineData("ns::Foo<a::B>", "Foo")]
    [InlineData("Foo<int>::bar", "bar")]
    [InlineData("Map<K, std::pair<A::X, B>>::find", "find")]
    [InlineData("vector<int>", "vector")]
    public void MemberOfQualified_SkipsTemplateArguments(string query, string expected)
    {
        var (name, note) = ReferenceMerge.MemberOfQualified(query);
        Assert.Equal(expected, name);
        Assert.NotNull(note);
        Assert.Contains($"\"{query}\"", note);
    }

    // Review round 3: the `<` in an operator NAME is not a template bracket - `Foo::operator<<` became `operator`, which
    // matches every operator overload in the repo.
    [Theory]
    [InlineData("Foo::operator<<", "operator<<")]
    [InlineData("ns::operator<=>", "operator<=>")]
    [InlineData("A<int>::operator<=", "operator<=")]
    [InlineData("Foo::operator<", "operator<")]
    public void MemberOfQualified_OperatorNames_KeepTheirAngleBrackets(string query, string expected) =>
        Assert.Equal(expected, ReferenceMerge.MemberOfQualified(query).Name);

    [Theory]
    [InlineData("plain_name")]
    [InlineData("a::")]
    [InlineData("operator<<")]
    [InlineData("operator<")]
    public void MemberOfQualified_LeavesOtherNamesAlone(string query) =>
        Assert.Equal((query, (string?)null), ReferenceMerge.MemberOfQualified(query));

    // Review round 2, finding 4: the CLI printed the rewrite note only on its main path, so its early answers (name too
    // short, no index) never said the query had been changed. The MCP tool always says so.
    [Fact]
    public void Cli_Refs_QualifiedQuery_EarlyAnswers_SayTheNameWasRewritten()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("w.cpp", "struct Widget { int go(); };\n");
        var (_, noIndex) = RunCliWithExit(cli, "refs", repo.Root, "Widget::spin");
        Assert.Contains("\"Widget::spin\"", noIndex);

        var (bt, bs, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(repo.Root);
        bt.Dispose(); bs.Dispose();
        var (_, tooShort) = RunCliWithExit(cli, "refs", repo.Root, "Widget::go");
        Assert.Contains("under 3 characters", tooShort);
        Assert.Contains("\"Widget::go\"", tooShort);
    }

    // On a case-sensitive tree (Linux Samba shares, WSL, fsutil-enabled folders) Reg.h and reg.h are different files. The
    // comment/string classifier and the dedup keys compared paths ignoring case, so one file's comment spans were applied to
    // the other and a real use was dropped (review finding 13; real netfilter pairs like xt_DSCP.c / xt_dscp.c).
    [Fact]
    public void Mcp_CaseSensitiveTree_FilesDifferingOnlyInCase_AreKeptApart()
    {
        using var repo = new TempRepo();
        var inc = System.IO.Path.Combine(repo.Root, "inc");
        System.IO.Directory.CreateDirectory(inc);
        var fsutil = Process.Start(new ProcessStartInfo("fsutil", $"file setCaseSensitiveInfo \"{inc}\" enable")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        fsutil.WaitForExit();
        // Already case-sensitive (Linux, macOS case-sensitive volumes) or made so by fsutil. Otherwise this check can't
        // run here; LexicalSpanFilterTests.SpanCache_KeysPathsExactly covers the same key on a case-insensitive tree.
        System.IO.File.WriteAllText(System.IO.Path.Combine(inc, "Probe"), "");
        if (System.IO.File.Exists(System.IO.Path.Combine(inc, "probe"))) return;
        System.IO.File.Delete(System.IO.Path.Combine(inc, "Probe"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(inc, "Reg.h"), "/* uses MAGIC_REG */\nint pad;\n"); // MAGIC_REG at the same column as in reg.h
        System.IO.File.WriteAllText(System.IO.Path.Combine(inc, "reg.h"), "int x = MAGIC_REG;\n");
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            var r = CodeCompass.Mcp.CodeCompassTools.FindReferences("MAGIC_REG");
            Assert.Contains("inc/reg.h:1:9", r);       // the real use
            Assert.DoesNotContain("inc/Reg.h", r);     // only a comment there
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    // In-process (so coverage sees it): the reviewer's repro. Uses of a struct type were dropped as if they were its
    // definition - `refs node` returned 0. Only the line with the body is the definition.
    [Fact]
    public void Mcp_StructTypeUses_AreReferences_OnlyTheBodyIsTheDefinition()
    {
        using var repo = new TempRepo();
        repo.Write("node.h", "struct node { int v; struct node *next; };\nstruct node *node_new(void);\n");
        repo.Write("node.c", "#include \"node.h\"\n#include <stdlib.h>\nstruct node *node_new(void) { return malloc(sizeof(struct node)); }\nvoid node_free(struct node *n) { free(n); }\n");
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            var r = CodeCompass.Mcp.CodeCompassTools.FindReferences("node");
            Assert.Contains("node.h:1:29", r);            // struct node *next (inside the body)
            Assert.Contains("node.h:2:8", r);             // the prototype's return type
            Assert.Contains("node.c:3:8", r);
            Assert.Contains("node.c:3:59", r);            // sizeof(struct node)
            Assert.Contains("node.c:4:23", r);            // parameter type
            Assert.DoesNotContain("node.h:1:8", r);       // the definition itself
            var d = CodeCompass.Mcp.CodeCompassTools.FindDefinition("node");
            Assert.Contains("node.h:1", d);
            Assert.DoesNotContain("node.c:", d);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    // The scan budget must count REFERENCES, not raw substring hits: noisy files (longer identifiers containing the name,
    // C# files the semantic pass already answered) that sort first used to exhaust it, so the real C use later in path
    // order was never read and the answer said "No references found" (review finding 2).
    [Fact]
    public void Mcp_NoisyFilesFirst_DoNotStarveTheRealReference()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 17; i++)
            repo.Write($"a{i:D2}.cs", "class T" + i + " { long ElapsedTicks; void M() { " +
                string.Concat(Enumerable.Range(0, 60).Select(_ => "ElapsedTicks++; ")) + "} }\n");
        for (int i = 0; i < 16; i++)
            repo.Write($"b{i:D2}.c", string.Concat(Enumerable.Range(0, 40).Select(k => $"int x{k} = sizeof(int) + Ticks_total_{k};\n")));
        repo.Write("z.c", "int Ticks(int v);\nint use(void) { return Ticks(5); }\n");
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            var r = CodeCompass.Mcp.CodeCompassTools.FindReferences("Ticks");
            Assert.Contains("z.c:2:24", r);
            Assert.DoesNotContain("No references found", r);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    // Definitions in languages without a semantic pass are skipped too - the description promises "never the definition".
    [Fact]
    public void Mcp_PythonDefinition_IsNotAReference()
    {
        using var repo = new TempRepo();
        repo.Write("calc.py", "def compute_total(items):\n    return sum(items)\n\nprint(compute_total([1, 2]))\n");
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            var r = CodeCompass.Mcp.CodeCompassTools.FindReferences("compute_total");
            Assert.Contains("calc.py:4:", r);
            Assert.DoesNotContain("calc.py:1:", r);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    // C# stays semantic. A pre-cancelled token aborts the Roslyn query (moved from the deleted clang test file).
    [Fact]
    public void RoslynAnalyzer_CancelledToken_BailsPromptly()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class C { void M() { Helper(); } void Helper() { } } }");

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new RoslynCSharpAnalyzer(repo.Root).FindReferences("Helper", 200, cts.Token));
    }
}
