using System.Linq;
using System.Text;
using CodeCompass.Core.Ignore;
using Xunit;

namespace CodeCompass.Core.Tests;

public class IgnoreRulesTests
{
    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".git")]
    [InlineData(".GIT")] // case-insensitive
    [InlineData(".claude")]  // other AI tools' index/cache dirs - indexing them pollutes results
    [InlineData(".cursor")]
    [InlineData(".aider")]
    public void IgnoredDirectories_AreSkipped(string name)
    {
        Assert.True(new IgnoreRules().IsIgnoredDirectory(name));
    }

    [Theory]
    [InlineData("src")]
    [InlineData("MyProject")]
    public void NormalDirectories_AreKept(string name)
    {
        Assert.False(new IgnoreRules().IsIgnoredDirectory(name));
    }

    // Query-time guard: a (repo-relative) path is ignored if ANY directory segment is an ignored dir OR the
    // extension is an ignored asset type. Lets query results drop stale-index pollution the current walker
    // would never index - so "ignored at index time" also means "excluded at query time".
    [Theory]
    [InlineData(".claude/index/tags.json", true)]     // rival AI-tool cache dump - the field pollution case
    [InlineData("node_modules/pkg/index.js", true)]   // ignored dir
    [InlineData("deep/a/b/.git/config", true)]         // ignored dir at any depth
    [InlineData("obj/Release/App.dll", true)]          // ignored dir (and ignored ext)
    [InlineData("assets/logo.png", true)]              // ignored extension
    [InlineData("data/blob.bin", true)]                // ignored extension
    [InlineData("src/App.cs", false)]                  // normal source
    [InlineData("docs/readme.md", false)]              // .md is searchable
    [InlineData("a/b/c/Widget.cpp", false)]
    [InlineData("", false)]
    // Edge cases the fix must get right (exact SEGMENT match, not substring; case-insensitive; both separators;
    // directory segments only - the filename is not treated as a dir):
    [InlineData("binary/logo.cs", false)]              // "binary" merely CONTAINS "bin" - not an ignored segment
    [InlineData("objects/model.cs", false)]            // "objects" is not "obj"
    [InlineData("node_modules\\pkg\\index.js", true)]  // backslash separators split too
    [InlineData("BIN/App.cs", true)]                   // dir match is case-insensitive
    [InlineData("src/bin/App.cs", true)]               // ignored dir nested under a normal one
    [InlineData("bin", false)]                          // a bare filename "bin" is NOT a directory segment
    public void IsIgnoredPath_MatchesWalkerExclusions(string relativePath, bool expected)
        => Assert.Equal(expected, new IgnoreRules().IsIgnoredPath(relativePath));

    [Theory]
    [InlineData("app.dll")]
    [InlineData("photo.PNG")]
    [InlineData("archive.zip")]
    public void BinaryExtensions_AreIgnored(string fileName)
    {
        Assert.True(new IgnoreRules().IsIgnoredFile(fileName, 100));
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("main.cpp")]
    [InlineData("script.py")]
    public void SourceExtensions_AreKept(string fileName)
    {
        Assert.False(new IgnoreRules().IsIgnoredFile(fileName, 100));
    }

    [Fact]
    public void OversizedFiles_AreIgnored()
    {
        var rules = new IgnoreRules(maxFileSizeBytes: 1024);
        Assert.True(rules.IsIgnoredFile("big.cs", 2048));
        Assert.False(rules.IsIgnoredFile("small.cs", 512));
    }

    [Fact]
    public void LooksBinary_DetectsNulByte()
    {
        Assert.True(IgnoreRules.LooksBinary(new byte[] { 65, 66, 0, 67 }));
        Assert.False(IgnoreRules.LooksBinary(Encoding.ASCII.GetBytes("plain text")));
    }

    [Fact]
    public void LooksBinary_AllowsBomMarkedUnicodeText()
    {
        // UTF-16/UTF-32 encode ASCII with NUL bytes; a byte-order mark identifies them as text, so the
        // NUL check must NOT flag them - otherwise real UTF-16 source (e.g. a Windows-written .cmm) is
        // wrongly skipped as binary.
        static byte[] Bom(Encoding e, string s) => e.GetPreamble().Concat(e.GetBytes(s)).ToArray();
        Assert.False(IgnoreRules.LooksBinary(Bom(Encoding.Unicode, "hello")));           // UTF-16 LE
        Assert.False(IgnoreRules.LooksBinary(Bom(Encoding.BigEndianUnicode, "hello")));  // UTF-16 BE
        Assert.False(IgnoreRules.LooksBinary(Bom(Encoding.UTF32, "hello")));             // UTF-32 LE
        Assert.False(IgnoreRules.LooksBinary(Bom(Encoding.UTF8, "hello")));              // UTF-8 BOM

        // A genuine binary (NUL bytes, no text BOM) still reads as binary.
        Assert.True(IgnoreRules.LooksBinary(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x1A }));
    }
}
