using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// Unit coverage for the comment/string classifier behind the find_references lexical backfill (the v1.0.212
// precision fix). A whole-word hit that lands inside a comment or string/char literal is NOT a reference; one in
// real code (including inside an interpolation hole) IS. C# is classified by Roslyn's lexer, C/C++ by a
// conservative C-family scanner. The bias everywhere is: when in doubt, DON'T suppress (never drop a real ref).
public class LexicalSpanFilterTests
{
    private static int Col(string line, string needle) => line.IndexOf(needle, System.StringComparison.Ordinal) + 1; // 1-based

    // Paths that differ only in case are different files on a case-sensitive tree (Reg.h / reg.h), so the span cache must
    // key them apart (review finding 13). This checks the key itself on ANY filesystem: on a case-insensitive one both
    // spellings open the same file, so after the content changes, a lookup under the other spelling must re-read it - an
    // ignore-case cache would serve the first spelling's spans. (The end-to-end test in CppNameRefsTests needs a
    // case-sensitive folder, which not every machine can make.)
    [Fact]
    public void SpanCache_KeysPathsExactly()
    {
        using var repo = new TempRepo();
        repo.Write("Reg.h", "/* uses MAGIC_REG */\n");
        var f = new LexicalSpanFilter();
        Assert.True(f.IsInCommentOrString(System.IO.Path.Combine(repo.Root, "Reg.h"), 1, 9));

        repo.Write("Reg.h", "int x = MAGIC_REG;\n");
        var other = System.IO.Path.Combine(repo.Root, "reg.h");
        if (!System.IO.File.Exists(other)) return; // case-sensitive tree: "reg.h" is another (absent) file - covered end to end
        Assert.False(f.IsInCommentOrString(other, 1, 9));
    }

    [Fact]
    public void CSharp_Comments_And_Strings_AreSuppressed_CodeIsNot()
    {
        using var repo = new TempRepo();
        var lines = new[]
        {
            "namespace N {",                                  // 1
            "  /// Sends to <see cref=\"Foo\"/>.",            // 2  doc comment
            "  // plain Foo mention",                          // 3  line comment
            "  class Bar {",                                   // 4
            "    string s = \"Foo value\";",                  // 5  string literal
            "    int Foo;",                                    // 6  real code
            "    string t = $\"n={Foo}\";",                   // 7  interpolation hole is code
            "  }",                                              // 8
            "}",                                                // 9
        };
        repo.Write("a.cs", string.Join("\n", lines) + "\n");
        var path = System.IO.Path.Combine(repo.Root, "a.cs");
        var f = new LexicalSpanFilter();

        Assert.True(f.IsInCommentOrString(path, 2, Col(lines[1], "Foo")));   // in <see cref> doc comment
        Assert.True(f.IsInCommentOrString(path, 3, Col(lines[2], "Foo")));   // in // line comment
        Assert.True(f.IsInCommentOrString(path, 5, Col(lines[4], "Foo")));   // in string literal
        Assert.False(f.IsInCommentOrString(path, 6, Col(lines[5], "Foo")));  // real declaration
        Assert.False(f.IsInCommentOrString(path, 7, Col(lines[6], "Foo")));  // inside $"...{Foo}..." hole = code
    }

    [Fact]
    public void CFamily_Comments_Strings_Chars_RawStrings_AreSuppressed_CodeIsNot()
    {
        using var repo = new TempRepo();
        var lines = new[]
        {
            "// widget in a line comment",                     // 1
            "const char* s = \"widget text\";",              // 2  string
            "/* block",                                        // 3  block comment start
            "   widget inside block */",                       // 4  still comment
            "char c = 'w';",                                   // 5  char literal (not widget, just exercises ')
            "const char* r = R\"(widget raw)\";",            // 6  raw string
            "int widget_fn(void){ return 0; }",               // 7  real code
        };
        repo.Write("a.c", string.Join("\n", lines) + "\n");
        var path = System.IO.Path.Combine(repo.Root, "a.c");
        var f = new LexicalSpanFilter();

        Assert.True(f.IsInCommentOrString(path, 1, Col(lines[0], "widget")));  // line comment
        Assert.True(f.IsInCommentOrString(path, 2, Col(lines[1], "widget")));  // string
        Assert.True(f.IsInCommentOrString(path, 4, Col(lines[3], "widget")));  // multi-line block comment body
        Assert.True(f.IsInCommentOrString(path, 6, Col(lines[5], "widget")));  // raw string body
        Assert.False(f.IsInCommentOrString(path, 7, Col(lines[6], "widget"))); // real function definition
    }

    // Regression (review finding): a C++ digit separator (10'000, 0x1'0000, 1'000'000) must NOT be read as a
    // char-literal opener. If it were, an odd number of apostrophes opens a literal that swallows the rest of the
    // line and DROPS a real reference after it - the catastrophic false-negative the invariant forbids.
    [Theory]
    [InlineData("const int kMax = 10'000; Widget w;")]
    [InlineData("auto a = 0x1'0000; Widget w;")]
    [InlineData("auto b = 1'000'000; Widget w;")]
    [InlineData("auto c = 0b1'0; Widget w;")]
    public void CFamily_DigitSeparator_IsNotCharLiteral_RealRefSurvives(string line)
    {
        using var repo = new TempRepo();
        repo.Write("a.cpp", line + "\n");
        var path = System.IO.Path.Combine(repo.Root, "a.cpp");
        var f = new LexicalSpanFilter();
        Assert.False(f.IsInCommentOrString(path, 1, Col(line, "Widget")));   // real ref must survive
    }

    // Regression (review finding): an identifier ending in 'R' abutting a string ('FOOR"..."') must NOT be read as
    // a C++ raw string - a bogus raw delimiter never closes and swallows real code to EOL. A genuine raw string
    // (bare R"(...)" or an encoding-prefixed u8R"(...)") still is.
    [Fact]
    public void CFamily_IdentifierEndingInR_IsNotRawString_RealRefSurvives()
    {
        using var repo = new TempRepo();
        var line = "auto s = FOOR\"x\"; Widget();";                          // FOOR is an identifier, not R"..."
        repo.Write("a.cpp", line + "\n");
        var path = System.IO.Path.Combine(repo.Root, "a.cpp");
        var f = new LexicalSpanFilter();
        Assert.False(f.IsInCommentOrString(path, 1, Col(line, "Widget")));   // real ref must survive
    }

    // A '\' escape inside a string (an escaped char AND a trailing-backslash line continuation) must keep the
    // scanner INSIDE the string - so a whole-word hit after the escape, or on the continued line, is still
    // classified as string and suppressed. Pins the escape/continuation branches of the C-family string scanner.
    [Fact]
    public void CFamily_StringWithEscapesAndContinuation_WidgetStaysSuppressed()
    {
        using var repo = new TempRepo();
        var lines = new[]
        {
            "const char* a = \"x\\ty widget\";",   // 1  escaped \t then widget - still inside the string
            "const char* b = \"abc\\",              // 2  trailing backslash = line continuation...
            "widget more\";",                        // 3  ...the string continues here, widget still inside it
        };
        repo.Write("a.c", string.Join("\n", lines) + "\n");
        var path = System.IO.Path.Combine(repo.Root, "a.c");
        var f = new LexicalSpanFilter();
        Assert.True(f.IsInCommentOrString(path, 1, Col(lines[0], "widget")));  // after an escaped char, still string
        Assert.True(f.IsInCommentOrString(path, 3, Col(lines[2], "widget")));  // after \-continuation, still string
    }

    // Fail-OPEN invariant: a stray apostrophe that does NOT close within the short char-literal bound is NOT a
    // char literal - it's code. The scanner must bail to code rather than run to EOL, or it would swallow and
    // suppress a real reference after it (the catastrophic false-negative the design forbids).
    [Fact]
    public void CFamily_UnterminatedCharLiteral_FailsOpenToCode_RealRefSurvives()
    {
        using var repo = new TempRepo();
        var line = "x = ' no closing quote here at all; Widget w;";   // ' preceded by space, never closes in bound
        repo.Write("a.cpp", line + "\n");
        var path = System.IO.Path.Combine(repo.Root, "a.cpp");
        var f = new LexicalSpanFilter();
        Assert.False(f.IsInCommentOrString(path, 1, Col(line, "Widget")));   // real ref must survive
    }

    // Review 2026-10-05: C/C++ references are a name search now, so this scanner alone decides code vs comment/string for
    // every C/C++ hit. Four shapes it misread, each silently dropping a real reference:

    // (1) a backslash line-continuation inside a string, in a CRLF file (only backslash+LF was recognized).
    [Fact]
    public void CFamily_StringContinuation_WithCrlf_EndsWhereTheStringDoes()
    {
        using var repo = new TempRepo();
        var lines = new[] { "const char* b = \"abc\\", "def\"; int x1 = eps_fn(1);" };
        repo.Write("a.c", string.Join("\r\n", lines) + "\r\n");
        var f = new LexicalSpanFilter();
        Assert.False(f.IsInCommentOrString(System.IO.Path.Combine(repo.Root, "a.c"), 2, Col(lines[1], "eps_fn")));
    }

    // (2) encoding-prefixed char literals (L'"', L'/', u'x', U'x', u8'x'): the prefix is not an identifier abutting a quote.
    [Theory]
    [InlineData("if (c == L'\"') return eps_fn(3);", "eps_fn")]
    [InlineData("if (sepc == L'/' || sepc == L'\\\\') eps_fn(4);", "eps_fn")]
    [InlineData("auto a = u'\"'; eps_fn(5);", "eps_fn")]
    [InlineData("auto a = U'\"'; eps_fn(6);", "eps_fn")]
    [InlineData("auto a = u8'\"'; eps_fn(7);", "eps_fn")]
    public void CFamily_PrefixedCharLiteral_ClosesNormally_RealRefSurvives(string line, string token)
    {
        using var repo = new TempRepo();
        repo.Write("a.cpp", line + "\n");
        var f = new LexicalSpanFilter();
        Assert.False(f.IsInCommentOrString(System.IO.Path.Combine(repo.Root, "a.cpp"), 1, line.LastIndexOf(token) + 1));
    }

    // (3) classic-Mac CR-only line endings: a // comment ends at the CR. (The index counts lines by LF, so the whole file is
    // "line 1" to it - columns run on across the CRs.)
    [Fact]
    public void CFamily_CrOnlyLineEndings_CommentEndsAtTheCr()
    {
        using var repo = new TempRepo();
        var text = "// lead comment\rint a = eps_fn(1);\rint b = eps_fn(2);\r";
        repo.Write("a.c", text);
        var f = new LexicalSpanFilter();
        var path = System.IO.Path.Combine(repo.Root, "a.c");
        Assert.False(f.IsInCommentOrString(path, 1, text.IndexOf("eps_fn") + 1));
        Assert.False(f.IsInCommentOrString(path, 1, text.LastIndexOf("eps_fn") + 1));
        Assert.True(f.IsInCommentOrString(path, 1, text.IndexOf("lead") + 1));
    }

    // (4) an unterminated string fails OPEN, like an unterminated char literal: a stray quote must not hide the rest of the
    // line.
    [Fact]
    public void CFamily_UnterminatedString_FailsOpenToCode_RealRefSurvives()
    {
        using var repo = new TempRepo();
        var line = "STRINGIFY(\"oops); eps_fn(1);";
        repo.Write("a.c", line + "\n");
        var f = new LexicalSpanFilter();
        Assert.False(f.IsInCommentOrString(System.IO.Path.Combine(repo.Root, "a.c"), 1, Col(line, "eps_fn")));
    }

    // Fail-OPEN invariant for raw strings: an R"-opener with no '(' before end-of-line is malformed. ScanRawString
    // must bail (recording only what it consumed) rather than consume to EOF, so a real reference on a later line
    // is not swallowed.
    [Fact]
    public void CFamily_MalformedRawString_BailsWithoutSwallowing_RealRefSurvives()
    {
        using var repo = new TempRepo();
        var lines = new[]
        {
            "const char* s = R\"nodelim",   // 1  R" opener but no '(' before EOL -> malformed, must bail here
            "int Widget(void){return 0;}",  // 2  real code on the next line - must NOT be suppressed
        };
        repo.Write("a.c", string.Join("\n", lines) + "\n");
        var path = System.IO.Path.Combine(repo.Root, "a.c");
        var f = new LexicalSpanFilter();
        Assert.False(f.IsInCommentOrString(path, 2, Col(lines[1], "Widget")));   // real ref must survive
    }

    [Fact]
    public void NonCoveredLanguage_IsNeverSuppressed()
    {
        using var repo = new TempRepo();
        repo.Write("a.py", "# widget in a python comment\n");
        var path = System.IO.Path.Combine(repo.Root, "a.py");
        var f = new LexicalSpanFilter();
        // No semantic promise about comments for non-covered languages: behaviour unchanged (keep the hit).
        Assert.False(f.IsInCommentOrString(path, 1, 3));
    }

    [Fact]
    public void UnreadableFile_SuppressesNothing()
    {
        var f = new LexicalSpanFilter();
        Assert.False(f.IsInCommentOrString(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "does-not-exist-xyz.cs"), 1, 1));
    }

    // On a generated corpus where ~3,100 files each DEFINE the queried name, refs read and lexed every one of them for
    // comments/strings before noticing the hit was an already-known definition - over a second of a 4 s name search. A hit
    // already listed (a definition or a semantic hit) must be dropped without classifying its file; a new one still is.
    [Fact]
    public void ReferenceAccept_AlreadyListedHit_IsDroppedWithoutClassifyingItsFile()
    {
        using var repo = new TempRepo();
        repo.Write("def.c", "int compute17(int x) { return x + 17; }\n");
        repo.Write("use.c", "int y = compute17(1);\n");
        var filter = new LexicalSpanFilter("compute17");
        var listed = new HashSet<string>(System.StringComparer.Ordinal) { "def.c:1:5" };
        var accept = ReferenceMerge.ReferenceAccept(repo.Root, "compute17".Length, csharpIncomplete: false, filter, listed, rel => rel);

        Assert.False(accept(new CodeCompass.Core.Indexing.SearchMatch("def.c", 1, 5, "int compute17(int x) { return x + 17; }")));
        Assert.Equal(0, filter.FilesClassified);

        Assert.True(accept(new CodeCompass.Core.Indexing.SearchMatch("use.c", 1, 9, "int y = compute17(1);")));
        Assert.Equal(1, filter.FilesClassified);
    }
}
