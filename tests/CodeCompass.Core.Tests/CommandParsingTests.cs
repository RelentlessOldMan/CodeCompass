using System.Collections.Generic;
using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// Pins ClangCppAnalyzer's compile_commands "command"-string parsing - the pure logic that turns a raw
// build command into the argv clang is invoked with. The XML docs on both methods call out the exact bugs
// they guard (a naive Split(' ') shredding -I "C:\Program Files\...", and dropping a -D whose value ends
// with the source basename). Neither was reachable except through a real libclang parse, so this fills the
// gap with fast, deterministic, native-free coverage.
public class CommandParsingTests
{
    [Fact]
    public void TokenizeCommand_KeepsQuotedPathWithSpaces_AsOneToken()
    {
        var t = ClangCppAnalyzer.TokenizeCommand(@"clang -I ""C:\Program Files\SDK\inc"" -c foo.c");
        Assert.Equal(new[] { "clang", "-I", @"C:\Program Files\SDK\inc", "-c", "foo.c" }, t);
    }

    [Fact]
    public void TokenizeCommand_PlainQuotesGroup_AndAreStripped()
    {
        // Plain double quotes GROUP their contents (interior space protected) and are removed from the token:
        // -DMSG="hi there" -> one token -DMSG=hi there. This is the branch that keeps -I "C:\Program Files\..."
        // whole; a naive Split(' ') would shred it.
        var t = ClangCppAnalyzer.TokenizeCommand(@"clang -DMSG=""hi there"" -c foo.c");
        Assert.Contains("-DMSG=hi there", t);
        Assert.DoesNotContain(t, x => x == "there");
    }

    [Fact]
    public void TokenizeCommand_EscapedQuote_ProducesLiteralQuoteCharacter()
    {
        // A backslash-escaped quote (\") is a LITERAL quote character embedded in the token - distinct from a
        // grouping quote. -DNAME=\"x\" -> one token -DNAME="x".
        var t = ClangCppAnalyzer.TokenizeCommand(@"clang -DNAME=\""x\"" -c foo.c");
        Assert.Contains(@"-DNAME=""x""", t);
    }

    [Fact]
    public void TokenizeCommand_TabsSplitLikeSpaces()
    {
        var t = ClangCppAnalyzer.TokenizeCommand("clang\t-c\tfoo.c");
        Assert.Equal(new[] { "clang", "-c", "foo.c" }, t);
    }

    [Fact]
    public void TokenizeCommand_UnterminatedQuote_StillFlushesLastToken()
    {
        // A malformed (unterminated) quote must not drop the trailing token entirely.
        var t = ClangCppAnalyzer.TokenizeCommand(@"clang -c ""foo.c");
        Assert.Equal(new[] { "clang", "-c", "foo.c" }, t);
    }

    [Fact]
    public void CleanArgs_DropsCompilerOutputAndSource_KeepsFlags()
    {
        var tokens = new List<string> { "clang", "-c", "-o", "out.o", "-DX=path/to/foo.c", "-Iinc", "foo.c" };
        var args = ClangCppAnalyzer.CleanArgs(tokens, "foo.c");
        // compiler[0], -c, -o+arg, and the positional source are dropped...
        Assert.DoesNotContain("clang", args);
        Assert.DoesNotContain("-c", args);
        Assert.DoesNotContain("-o", args);
        Assert.DoesNotContain("out.o", args);
        Assert.DoesNotContain("foo.c", args); // the positional source file
        // ...but a -D whose VALUE merely ends with the basename must survive (dropping it changes the parse).
        Assert.Contains("-DX=path/to/foo.c", args);
        Assert.Contains("-Iinc", args);
        Assert.Equal(new[] { "-DX=path/to/foo.c", "-Iinc" }, args);
    }

    [Fact]
    public void CleanArgs_MatchesSourceByBasename_AcrossDirectorySeparators()
    {
        // The positional source may be given with a path prefix; it's still the source (basename match).
        var tokens = new List<string> { "clang", "-Iinc", @"src\sub\foo.c" };
        var args = ClangCppAnalyzer.CleanArgs(tokens, "foo.c");
        Assert.Equal(new[] { "-Iinc" }, args);
    }

    // Review P0-3: the compile DB comes from the UNTRUSTED repo. Code loading, filesystem remapping, raw argument
    // forwarding and file-writing flags must never reach libclang; parse-shaping flags must.
    [Fact]
    public void CleanArgs_DropsCodeLoadingRemappingAndFileWritingFlags()
    {
        var tokens = new List<string>
        {
            "clang", "-Xclang", "-load", "-Xclang", @"C:\repo\evil.dll", "-fplugin=evil.dll", "-fpass-plugin=x.dll",
            "-ivfsoverlay", "overlay.yaml", "-fmodules", "-fmodules-cache-path=C:\\anywhere", "-MD", "-MF", "deps.d",
            "-Wp,-MD,C:\\x.d", "-Wl,--foo", "-mllvm", "-info-output-file=C:\\x", "@flags.rsp", "-B", "C:\\tools",
            "-ftime-trace", "-fcrash-diagnostics-dir=C:\\x", "-include-pch", "pch.pch", "foo.c",
        };
        var args = ClangCppAnalyzer.CleanArgs(tokens, "foo.c");
        Assert.Empty(args);
    }

    [Fact]
    public void CleanArgs_KeepsEveryParseShapingFlag_InBothSpellings()
    {
        var tokens = new List<string>
        {
            "clang++", "-D", "A=1", "-DB", "-U", "C", "-I", @"C:\Program Files\SDK\inc", "-Iinc", "-isystem", "sys",
            "-iquote", "q", "-include", "config.h", "-std=c++20", "-x", "c++", "--target=x86_64-pc-windows-msvc",
            "-fms-extensions", "-fms-compatibility", "-fno-exceptions", "-m64", "-Wno-everything", "-O2", "-pthread",
            "-DPROFILE_BUILD=1", "-c", "foo.cpp",
        };
        var args = ClangCppAnalyzer.CleanArgs(tokens, "foo.cpp");
        Assert.Equal(new[]
        {
            "-D", "A=1", "-DB", "-U", "C", "-I", @"C:\Program Files\SDK\inc", "-Iinc", "-isystem", "sys", "-iquote", "q",
            "-include", "config.h", "-std=c++20", "-x", "c++", "--target=x86_64-pc-windows-msvc", "-fms-extensions",
            "-fms-compatibility", "-fno-exceptions", "-m64", "-Wno-everything", "-O2", "-pthread", "-DPROFILE_BUILD=1",
        }, args); // a DEFINE that merely contains "profile" is a define, not a profiling flag
    }

    [Fact]
    public void CleanArgs_TranslatesMsvcSpellings_OnlyForAnMsvcDriver()
    {
        var cl = new List<string> { @"C:\VS\bin\cl.exe", "/DWIN32", "/D", "UNICODE", "/Iinc", "/FIpch.h", "/std:c++17", "/EHsc", "/c", "foo.cpp" };
        Assert.Equal(new[] { "-DWIN32", "-DUNICODE", "-Iinc", "-includepch.h", "-std=c++17" },
                     ClangCppAnalyzer.CleanArgs(cl, "foo.cpp"));

        // On a GNU driver a leading slash is a Unix PATH: /Data/x.c must not become a define.
        var gcc = new List<string> { "/usr/bin/gcc", "-Iinc", "/Data/proj/x.c", "/Users/me/y.c" };
        Assert.Equal(new[] { "-Iinc" }, ClangCppAnalyzer.CleanArgs(gcc, "x.c"));
    }

    [Fact]
    public void CleanArgs_TrailingDashO_WithNoArgument_DoesNotThrow()
    {
        // -o as the last token (no output following) must not eat a real flag or throw.
        var tokens = new List<string> { "clang", "-Iinc", "-o" };
        var args = ClangCppAnalyzer.CleanArgs(tokens, "foo.c");
        Assert.Equal(new[] { "-Iinc" }, args);
    }
}
