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

    [Fact]
    public void CleanArgs_TrailingDashO_WithNoArgument_DoesNotThrow()
    {
        // -o as the last token (no output following) must not eat a real flag or throw.
        var tokens = new List<string> { "clang", "-Iinc", "-o" };
        var args = ClangCppAnalyzer.CleanArgs(tokens, "foo.c");
        Assert.Equal(new[] { "-Iinc" }, args);
    }
}
