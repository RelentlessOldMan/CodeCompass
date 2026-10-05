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
