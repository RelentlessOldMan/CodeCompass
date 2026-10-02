using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// Regression for the "false zero": a C/C++ symbol with real call sites but an UNRESOLVED #include resolves
// to 0 semantic refs (clang can't see the declaration), yet the TU still PARSES (ParsedTus==CandidateTus),
// so a naive "incomplete = memory-stopped || parsed<candidate" check misses it and the lexical layer is
// suppressed -> a bare "0 references" on a symbol with real hits. refs must fall back to lexical whenever the
// semantic pass was incomplete for ANY reason, including unresolved includes.
public class ClangRefsFallbackTests
{
    private static void WriteMissingIncludeRepo(TempRepo repo)
    {
        for (int i = 0; i < 5; i++)
            repo.Write($"mod{i}.c", $"#include \"hwdefs_missing.h\"\nint use_{i}(void){{ return widget_reset({i}); }}\n");
    }

    // The signal the fallback keys on: a missing include yields unresolved-includes AND zero semantic refs.
    [Fact]
    public void Analyzer_MissingInclude_ReportsUnresolvedAndZeroSemantic()
    {
        using var repo = new TempRepo();
        WriteMissingIncludeRepo(repo);

        var r = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("widget_reset");
        Assert.Empty(r.Locations);                          // clang couldn't resolve it -> 0 semantic
        Assert.NotEmpty(r.UnresolvedIncludes);              // ...because the header is missing
        Assert.Contains("hwdefs_missing.h", r.UnresolvedIncludes);
        // The TU parses despite the error, so parsed==candidate: the OLD "incomplete" check would miss this.
        Assert.Equal(r.CandidateTus, r.ParsedTus);
    }

    // KeepGoing: a reference that sits AFTER a FATAL #include in the same TU must still be captured semantically.
    // Without CXTranslationUnit_KeepGoing clang aborts the parse at the missing header, so the later call site is
    // silently lost; with it, the parse continues and the (resolvable) reference is found - the unresolved include
    // is still recorded + disclosed. Uses a RESOLVABLE symbol (declared here + defined next door) so the thing
    // under test is purely "a reference past a fatal error survives", not an undeclared-symbol artifact (contrast
    // Analyzer_MissingInclude_ReportsUnresolvedAndZeroSemantic above, where the symbol is genuinely undeclared).
    [Fact]
    public void Analyzer_ReferenceAfterFatalInclude_StillFoundSemantically()
    {
        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        repo.Write("use.c", "int hot(int);\n#include \"missing_after_decl.h\"\nint use(void){ return hot(7); }\n");

        var r = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot");

        Assert.Contains("missing_after_decl.h", r.UnresolvedIncludes);   // the fatal error WAS hit (and disclosed)
        Assert.NotEmpty(r.Locations);                                    // ...yet the reference after it survived
        Assert.Contains(r.Locations, l => l.RelativePath.Replace('\\', '/').EndsWith("use.c")); // the post-#include call site
    }

    // End-to-end: the CLI `refs` command must NOT return a bare zero here - it must backfill lexical. Skips
    // softly if the CLI exe isn't built (bare `dotnet test`); the release gate builds it and runs this for real.
    [Fact]
    public void Cli_Refs_MissingInclude_FallsBackToLexical()
    {
        var cli = FindCliExe();
        if (cli is null) return;

        using var repo = new TempRepo();
        WriteMissingIncludeRepo(repo);

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        int code = RunCli(cli, "refs", repo.Root, out var stdout, out var stderr, "widget_reset");
        Assert.Equal(0, code);

        // The summary line is on stderr: "-- 0 C# + 0 C/C++ semantic + N lexical reference(s)". N must be > 0.
        var all = stdout + "\n" + stderr;
        var m = System.Text.RegularExpressions.Regex.Match(all, @"(\d+)\s+lexical reference");
        Assert.True(m.Success, $"expected a lexical-reference summary line; got:\n{all}");
        int lexical = int.Parse(m.Groups[1].Value);
        Assert.True(lexical > 0, $"CLI refs must backfill lexical on unresolved includes, not return a bare 0. Got {lexical}.\n{all}");
    }

    // The complement: when coverage is COMPLETE (no missing includes), the C/C++ files are fully resolved
    // semantically, so lexical must NOT backfill them (no double-counting, no comment/string noise). Guards
    // against over-firing the fallback - refs should be semantic-only here, with 0 lexical.
    [Fact]
    public void Cli_Refs_ResolvableSymbol_SemanticOnly_NoLexicalDoubleCount()
    {
        var cli = FindCliExe();
        if (cli is null) return;

        using var repo = new TempRepo();
        repo.Write("shared.h", "int foo(int);\n");
        repo.Write("foo.c", "#include \"shared.h\"\nint foo(int x){ return x + 1; }\n");
        for (int i = 0; i < 3; i++)
            repo.Write($"use{i}.c", $"#include \"shared.h\"\nint u{i}(void){{ return foo({i}); }}\n");

        Assert.Equal(0, RunCli(cli, "index", repo.Root, out _, out _));
        Assert.Equal(0, RunCli(cli, "refs", repo.Root, out var stdout, out var stderr, "foo"));

        var all = stdout + "\n" + stderr;
        var m = System.Text.RegularExpressions.Regex.Match(all, @"(\d+)\s+C#\s+\+\s+(\d+)\s+C/C\+\+ semantic\s+\+\s+(\d+)\s+lexical");
        Assert.True(m.Success, $"expected the refs summary; got:\n{all}");
        int cpp = int.Parse(m.Groups[2].Value), lexical = int.Parse(m.Groups[3].Value);
        Assert.True(cpp > 0, $"complete coverage should resolve C/C++ semantic refs to foo; got {cpp}.\n{all}");
        Assert.Equal(0, lexical); // covered files fully resolved => no lexical backfill, no double-count
    }

    // Unified teardown token (query side): a shutdown/re-point cancels the server token, and a long semantic
    // query must observe it and bail instead of pegging a core while teardown waits on the read lock. A
    // pre-cancelled token must make the C/C++ parse throw OperationCanceledException (ParallelOptions on the
    // multi-TU loop / the single-TU guard), not run to completion.
    [Fact]
    public void ClangAnalyzer_CancelledToken_BailsPromptly()
    {
        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        for (int i = 0; i < 6; i++)
            repo.Write($"use_{i}.c", $"int hot(int);\nint u{i}(void){{ return hot({i}); }}\n");

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel(); // pre-cancelled: the parse must bail

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot", candidateFiles: null, max: 200, ct: cts.Token));
    }

    // Same for the C# (Roslyn) query path: the token threads into SymbolFinder, so a pre-cancelled token aborts.
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
