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
