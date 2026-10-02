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
}
