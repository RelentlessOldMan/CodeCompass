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

    // The per-language backfill gate: a .cs file backfills lexical ONLY when the C# pass was incomplete; a
    // .c/.cpp/.h ONLY when the C/C++ pass was - so incompleteness in one language never over-fires lexical on
    // the other (which resolved cleanly). A language without a semantic analyzer is always eligible.
    [Fact]
    public void IsLexicalReference_IsPerLanguage()
    {
        Assert.True(ReferenceMerge.IsLexicalReference("a/b.cs", "Foo();", 1, 3, cppIncomplete: false, csharpIncomplete: true));
        Assert.False(ReferenceMerge.IsLexicalReference("a/b.cs", "Foo();", 1, 3, cppIncomplete: false, csharpIncomplete: false));
        Assert.False(ReferenceMerge.IsLexicalReference("a/b.cs", "Foo();", 1, 3, cppIncomplete: true, csharpIncomplete: false)); // cpp-incomplete must not backfill .cs
        Assert.True(ReferenceMerge.IsLexicalReference("a/b.cpp", "Foo();", 1, 3, cppIncomplete: true, csharpIncomplete: false));
        Assert.False(ReferenceMerge.IsLexicalReference("a/b.cpp", "Foo();", 1, 3, cppIncomplete: false, csharpIncomplete: true)); // cs-incomplete must not backfill .cpp
        Assert.True(ReferenceMerge.IsLexicalReference("a/b.py", "Foo()", 1, 3, cppIncomplete: false, csharpIncomplete: false)); // no analyzer -> always lexical
    }

    // End-to-end: a reference that exists ONLY inside an #if-guarded branch must not be a silent zero - refs
    // backfills it lexically and discloses. Skips softly if the CLI exe isn't built (bare `dotnet test`).
    [Fact]
    public void Cli_Refs_IfGuardedUse_FoundViaLexical_AndDisclosed()
    {
        var cli = FindCliExe();
        if (cli is null) return;

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

        Assert.Contains("caller.cs", all);                         // the #if-guarded use is recovered
        Assert.Contains("C# coverage INCOMPLETE", all);            // ...and the incompleteness is disclosed
        var m = System.Text.RegularExpressions.Regex.Match(all, @"(\d+)\s+lexical reference");
        Assert.True(m.Success && int.Parse(m.Groups[1].Value) > 0, $"expected a lexical backfill for the guarded ref:\n{all}");
    }

    // The complement: a clean C# repo (no conditional compilation) must resolve semantically only - no lexical
    // backfill, no disclosure. Guards against over-firing the fallback (which would re-introduce comment/string
    // false positives the semantic pass carefully excludes).
    [Fact]
    public void Cli_Refs_CleanCSharp_SemanticOnly_NoOverfire()
    {
        var cli = FindCliExe();
        if (cli is null) return;

        using var repo = new TempRepo();
        repo.Write("m.cs",
            "namespace N { public static class H2 {\n public static int Pong() => 1;\n public static int Use() => Pong();\n } }\n");

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "refs", repo.Root, out var stdout, out var stderr, "Pong"));
        var all = stdout + "\n" + stderr;

        Assert.DoesNotContain("C# coverage INCOMPLETE", all);       // nothing to disclose
        var m = System.Text.RegularExpressions.Regex.Match(all, @"(\d+)\s+C#\s+\+.*?(\d+)\s+lexical");
        Assert.True(m.Success, $"expected the refs summary; got:\n{all}");
        Assert.True(int.Parse(m.Groups[1].Value) > 0, $"the Use()->Pong() call should resolve semantically:\n{all}");
        Assert.Equal(0, int.Parse(m.Groups[2].Value));             // no lexical backfill on a clean C# repo
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

    private static string? FindCliExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var cliBin = Path.Combine(dir.FullName, "src", "CodeCompass.Cli", "bin");
            if (Directory.Exists(cliBin))
                return Directory.EnumerateFiles(cliBin, "CodeCompass.Cli.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }
}
