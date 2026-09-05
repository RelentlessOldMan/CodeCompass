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
}
