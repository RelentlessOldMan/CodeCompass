using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// Regression for the "fails quiet" C# bug: Roslyn builds its model with an EMPTY preprocessor-symbol set, so
// code in inactive #if/#elif branches is disabled text - invisible to semantic find_references / find_callees.
// A Login method that calls GetAllGroups() only inside `#if NET6_0_OR_GREATER` reported ZERO such refs/callees,
// silently. The honest fix: when a candidate .cs uses conditional compilation, treat the C# pass as incomplete
// -> refs backfills lexical (so the guarded use is still found) AND discloses; callees discloses. (Work box
// field report, independently reproduced, 2026-09-29.)
public class CSharpConditionalRefsTests
{
    [Theory]
    [InlineData("class C {\n#if DEBUG\n int X;\n#endif\n}", true)]
    [InlineData("#elif NET6_0_OR_GREATER", true)]
    [InlineData("#else", true)]
    [InlineData("  # if FOO", true)]                        // whitespace before/inside the directive
    [InlineData("class C { int X = 1; }", false)]
    [InlineData("#region Foo\n#endregion", false)]          // #region is not conditional compilation
    [InlineData("#define FOO\n#pragma warning disable", false)]
    [InlineData("#endif", false)]                            // a lone #endif carries no branch on its own
    [InlineData("", false)]
    public void HasCSharpConditionalCompilation_DetectsConditionals(string src, bool expected)
        => Assert.Equal(expected, SemanticCoverage.HasCSharpConditionalCompilation(src));

    // The per-language gate: a .cs name match counts ONLY when the C# semantic pass was incomplete (otherwise Roslyn
    // already answered for .cs); C/C++ and every other language are always answered by name.
    [Fact]
    public void IsLexicalReference_IsPerLanguage()
    {
        Assert.True(ReferenceMerge.IsLexicalReference("a/b.cs", "Foo();", 1, 3, csharpIncomplete: true));
        Assert.False(ReferenceMerge.IsLexicalReference("a/b.cs", "Foo();", 1, 3, csharpIncomplete: false));
        Assert.True(ReferenceMerge.IsLexicalReference("a/b.cpp", "Foo();", 1, 3, csharpIncomplete: false));
        Assert.True(ReferenceMerge.IsLexicalReference("a/b.h", "Foo();", 1, 3, csharpIncomplete: true));
        Assert.True(ReferenceMerge.IsLexicalReference("a/b.py", "Foo()", 1, 3, csharpIncomplete: false));
        Assert.False(ReferenceMerge.IsLexicalReference("a/b.log", "Foo()", 1, 3, csharpIncomplete: false)); // not code
    }

    // End-to-end: a reference that exists ONLY inside an #if-guarded branch must not be a silent zero - refs
    // backfills it lexically and discloses. Skips softly if the CLI exe isn't built (bare `dotnet test`).
    [Fact]
    public void Cli_Refs_IfGuardedUse_FoundViaLexical_AndDisclosed()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("helper.cs", "namespace N { public static class H { public static int Ping() => 1; } }\n");
        // The only reference to Ping() lives inside `#if NET6_0_OR_GREATER` - which Roslyn (empty preprocessor
        // set) treats as disabled text, so the semantic pass sees zero references to Ping.
        repo.Write("caller.cs",
            "namespace N {\n public static class C {\n#if NET6_0_OR_GREATER\n" +
            "  public static int A() => H.Ping();\n#endif\n  public static int B() => 2;\n }\n}\n");

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "refs", repo.Root, out var stdout, out var stderr, "Ping"));
        var all = stdout + "\n" + stderr;

        Assert.Contains("caller.cs", stdout);                      // the #if-guarded use is recovered
        Assert.Contains("C# coverage INCOMPLETE", stdout);         // ...and disclosed WITH the answer (stdout), so
                                                                   // `refs ... > out.txt` keeps it (field report)
        var m = System.Text.RegularExpressions.Regex.Match(all, @"(\d+)\s+name-matched reference");
        Assert.True(m.Success && int.Parse(m.Groups[1].Value) > 0, $"expected a lexical backfill for the guarded ref:\n{all}");
    }

    // The complement: a clean C# repo (no conditional compilation) must resolve semantically only - no lexical
    // backfill, no disclosure. Guards against over-firing the fallback (which would re-introduce comment/string
    // false positives the semantic pass carefully excludes).
    [Fact]
    public void Cli_Refs_CleanCSharp_SemanticOnly_NoOverfire()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("m.cs",
            "namespace N { public static class H2 {\n public static int Pong() => 1;\n public static int Use() => Pong();\n } }\n");

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "refs", repo.Root, out var stdout, out var stderr, "Pong"));
        var all = stdout + "\n" + stderr;

        Assert.DoesNotContain("C# coverage INCOMPLETE", all);       // nothing to disclose
        var m = System.Text.RegularExpressions.Regex.Match(all, @"(\d+)\s+C# semantic\s+\+\s+(\d+)\s+name-matched");
        Assert.True(m.Success, $"expected the refs summary; got:\n{all}");
        Assert.True(int.Parse(m.Groups[1].Value) > 0, $"the Use()->Pong() call should resolve semantically:\n{all}");
        Assert.Equal(0, int.Parse(m.Groups[2].Value));             // no lexical backfill on a clean C# repo
    }

    // v1.0.212 regression (field report v3): the lexical backfill is NOT comment/string-aware, so once the C#
    // pass is flagged incomplete (any candidate .cs uses #if), refs re-admits XML-doc <see cref="X"/> mentions -
    // the exact noise v1.0.100's marquee fix removed and the tool's own description promises to ignore. The fix
    // must keep the #if recall win (the guarded call site IS found) while dropping the doc-comment hit. The #if
    // and the doc comment live in the SAME file so the file is a trigram candidate (which is what trips the
    // incompleteness flag - the minimal repros in the report missed this by putting #if in an unrelated file).
    [Fact]
    public void Cli_Refs_DocCommentHit_NotReturned_EvenWhenIncomplete()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("kernel.cs", "namespace N { public class KernelMgrXyz { } }\n");
        // caller.cs: a #if branch (trips csharpIncomplete) whose guarded line is a REAL use of KernelMgrXyz, plus
        // a <see cref> doc comment that must NOT be counted. The guarded use is what the lexical backfill exists
        // to recover; the doc comment is what it must still exclude.
        repo.Write("caller.cs",
            "namespace N {\n public class User {\n" +
            "  /// Sends work to <see cref=\"KernelMgrXyz\"/>.\n" +            // DOC COMMENT - must be excluded
            "  public void B() { }\n" +
            "#if DEBUG\n" +
            "  public object A() { return new KernelMgrXyz(); }\n" +          // #if-guarded REAL use - must be found
            "#endif\n }\n}\n");

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "refs", repo.Root, out var stdout, out var stderr, "KernelMgrXyz"));
        var all = stdout + "\n" + stderr;

        Assert.Contains("C# coverage INCOMPLETE", all);                      // #if present -> incompleteness disclosed
        Assert.Contains("return new KernelMgrXyz()", all);                   // the #if-guarded use IS recovered (the win)
        Assert.DoesNotContain("see cref", all);                             // ...but the doc-comment mention is NOT a reference
    }

    // The honest-disclosure fix: the #if warning must NAME the file(s) that actually use conditional compilation,
    // so a user who greps the result files and finds no #if can see where it really is (report v3 finding: the
    // warning fired on a query whose result files contained zero #if, because a different candidate file had it).
    [Fact]
    public void Cli_Refs_IfDisclosure_NamesTheConditionalFile()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        // The symbol's definition + a clean use (no #if here).
        repo.Write("widget.cs", "namespace N { public class WidgetZzz { public static int Use() => new WidgetZzz().GetHashCode(); } }\n");
        // A SEPARATE candidate file (mentions WidgetZzz so it's in the trigram candidate set) that carries the #if.
        repo.Write("guarded.cs",
            "namespace N { public class GuardConsumer {\n#if DEBUG\n  WidgetZzz w;\n#endif\n } }\n");

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "refs", repo.Root, out var stdout, out var stderr, "WidgetZzz"));
        var all = stdout + "\n" + stderr;

        // Assert on the DISCLOSURE line specifically (the file also appears as a recovered result line, which is
        // not what we're testing): the "C# coverage INCOMPLETE" line must itself name the conditional file.
        var disclosure = all.Split('\n').FirstOrDefault(l => l.Contains("C# coverage INCOMPLETE"));
        Assert.NotNull(disclosure);
        Assert.Contains("guarded.cs", disclosure);                          // the disclosure names the real #if file
    }

    // #3 (field report v3): find_callees missed calls inside inactive #if branches (the forward/reverse
    // asymmetry - refs found the edge, callees didn't). Now those calls are recovered by re-lexing the disabled
    // region and resolving each name in-repo, shown in a segregated "by name" section, and the disclosure names
    // the conditional file. The ONLY call to Lib.Helper() lives inside #if DEBUG (inactive under Roslyn's empty
    // preprocessor set), so the semantic pass sees zero callees for Entry.
    [Fact]
    public void Cli_Callees_IfGuardedCall_RecoveredByName_AndDisclosed()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("lib.cs", "namespace N { public static class Lib { public static int Helper() => 1; } }\n");
        repo.Write("caller.cs",
            "namespace N {\n public static class C {\n  public static int Entry() {\n#if DEBUG\n" +
            "    return Lib.Helper();\n#endif\n    return 0;\n  }\n }\n}\n");

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "callees", repo.Root, out var stdout, out var stderr, "Entry"));
        var all = stdout + "\n" + stderr;

        Assert.Contains("C# coverage INCOMPLETE", all);                      // conditional compilation disclosed
        Assert.Contains("caller.cs", all);                                  // ...and the #if file is named
        Assert.Contains("resolved by NAME", all);                           // the segregated recovered section
        Assert.Contains("lib.cs", all);                                     // the recovered callee's definition
        Assert.Contains("Helper", all);
    }

    // Complement: a method with no conditional compilation recovers nothing and discloses nothing (no overfiring,
    // no "by name" noise on a clean call graph).
    [Fact]
    public void Cli_Callees_CleanMethod_NoRecoverySection_NoDisclosure()
    {
        var cli = TestCli.Find();

        using var repo = new TempRepo();
        repo.Write("m.cs",
            "namespace N { public static class H3 {\n public static int Leaf() => 1;\n public static int Top() => Leaf();\n } }\n");

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "callees", repo.Root, out var stdout, out var stderr, "Top"));
        var all = stdout + "\n" + stderr;

        Assert.Contains("Leaf", all);                                       // the real callee resolves semantically
        Assert.DoesNotContain("C# coverage INCOMPLETE", all);
        Assert.DoesNotContain("resolved by NAME", all);
    }

    // Field report v4: once #if makes the C# pass "incomplete", the lexical backfill re-admitted the symbol's own
    // DECLARATION lines (`public class X`, its ctor) as references - the semantic pass never counts declarations, so
    // the backfill must not either. The #if-guarded real use must still be recovered.
    private const string DeclRepoKernel = "namespace N {\n public class KernelZq {\n  public KernelZq() { }\n }\n}\n";
    private const string DeclRepoCaller =
        "namespace N {\n public class UserZq {\n  public object Plain() => new KernelZq();\n#if DEBUG\n" +
        "  public object Guarded() => new KernelZq();\n#endif\n }\n}\n";

    [Fact]
    public void Cli_Refs_Backfill_DoesNotCountDeclarations()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("kernel.cs", DeclRepoKernel);
        repo.Write("caller.cs", DeclRepoCaller);

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "refs", repo.Root, out var stdout, out var stderr, "KernelZq"));
        var all = stdout + "\n" + stderr;

        Assert.Contains("C# coverage INCOMPLETE", all);                // #if present -> backfill active
        Assert.Contains("Guarded() => new KernelZq()", all);           // the guarded use is still recovered
        Assert.DoesNotContain("public class KernelZq", all);           // ...but the declarations are not references
        Assert.DoesNotContain("public KernelZq()", all);
    }

    // MCP twins of the CLI tests above. The MCP tools merge semantic and name results with their OWN code
    // (CodeCompassTools), so the CLI tests don't protect them - and the MCP surface is what agents use.
    private static string Mcp(TempRepo repo, Func<string> query)
    {
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try { CodeCompass.Mcp.CodeCompassTools.Reindex(); return query(); }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void Mcp_FindReferences_DocCommentHit_NotReturned_EvenWhenIncomplete()
    {
        using var repo = new TempRepo();
        repo.Write("kernel.cs", "namespace N { public class KernelMgrXyz { } }\n");
        repo.Write("caller.cs",
            "namespace N {\n public class User {\n  /// Sends work to <see cref=\"KernelMgrXyz\"/>.\n  public void B() { }\n" +
            "#if DEBUG\n  public object A() { return new KernelMgrXyz(); }\n#endif\n }\n}\n");

        var r = Mcp(repo, () => CodeCompass.Mcp.CodeCompassTools.FindReferences("KernelMgrXyz"));
        Assert.Contains("C# coverage INCOMPLETE", r);
        Assert.Contains("return new KernelMgrXyz()", r);
        Assert.DoesNotContain("see cref", r);
    }

    [Fact]
    public void Mcp_FindReferences_IfDisclosure_NamesTheConditionalFile()
    {
        using var repo = new TempRepo();
        repo.Write("widget.cs", "namespace N { public class WidgetZzz { public static int Use() => new WidgetZzz().GetHashCode(); } }\n");
        repo.Write("guarded.cs", "namespace N { public class GuardConsumer {\n#if DEBUG\n  WidgetZzz w;\n#endif\n } }\n");

        var r = Mcp(repo, () => CodeCompass.Mcp.CodeCompassTools.FindReferences("WidgetZzz"));
        var at = r.IndexOf("C# coverage INCOMPLETE", StringComparison.Ordinal);
        Assert.True(at >= 0, r);
        var end = r.IndexOf("hides inactive-branch", at, StringComparison.Ordinal);
        Assert.True(end > at, r);
        Assert.Contains("guarded.cs", r[at..end]);                          // the disclosure itself names the #if file
    }

    [Fact]
    public void Mcp_FindReferences_CleanCSharp_NoDisclosure()
    {
        using var repo = new TempRepo();
        repo.Write("m.cs", "namespace N { public static class H2 {\n public static int Pong() => 1;\n public static int Use() => Pong();\n } }\n");

        var r = Mcp(repo, () => CodeCompass.Mcp.CodeCompassTools.FindReferences("Pong"));
        Assert.Contains("m.cs:3:", r);
        Assert.DoesNotContain("C# coverage INCOMPLETE", r);
    }

    [Fact]
    public void Mcp_FindCallees_IfGuardedCall_RecoveredByName_AndDisclosed()
    {
        using var repo = new TempRepo();
        repo.Write("lib.cs", "namespace N { public static class Lib { public static int Helper() => 1; } }\n");
        repo.Write("caller.cs",
            "namespace N {\n public static class C {\n  public static int Entry() {\n#if DEBUG\n" +
            "    return Lib.Helper();\n#endif\n    return 0;\n  }\n }\n}\n");

        var r = Mcp(repo, () => CodeCompass.Mcp.CodeCompassTools.FindCallees("Entry"));
        Assert.Contains("C# coverage INCOMPLETE", r);
        Assert.Contains("caller.cs", r);
        Assert.Contains("resolved by NAME", r);
        Assert.Contains("lib.cs", r);
        Assert.Contains("Helper", r);
    }

    [Fact]
    public void Mcp_FindCallees_CleanMethod_NoRecoverySection_NoDisclosure()
    {
        using var repo = new TempRepo();
        repo.Write("m.cs", "namespace N { public static class H3 {\n public static int Leaf() => 1;\n public static int Top() => Leaf();\n } }\n");

        var r = Mcp(repo, () => CodeCompass.Mcp.CodeCompassTools.FindCallees("Top"));
        Assert.Contains("Leaf", r);
        Assert.DoesNotContain("C# coverage INCOMPLETE", r);
        Assert.DoesNotContain("resolved by NAME", r);
    }

    [Fact]
    public void Mcp_FindReferences_Backfill_DoesNotCountDeclarations()
    {
        using var repo = new TempRepo();
        repo.Write("kernel.cs", DeclRepoKernel);
        repo.Write("caller.cs", DeclRepoCaller);
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            var r = CodeCompass.Mcp.CodeCompassTools.FindReferences("KernelZq");
            Assert.Contains("Guarded() => new KernelZq()", r);
            Assert.DoesNotContain("public class KernelZq", r);
            Assert.DoesNotContain("public KernelZq()", r);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    private static int RunCli(string exe, string cmd, string repo, out string stdout, out string stderr, string? arg = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(cmd);
        psi.ArgumentList.Add(repo);
        if (arg is not null) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(120_000);
        stdout = o.GetAwaiter().GetResult();
        stderr = e.GetAwaiter().GetResult();
        return p.ExitCode;
    }

}
