using CodeCompass.Semantics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// The positional-record prefilter scans each document's SourceText in chunks instead of copying it to a string on every
// find_references; a needle straddling a chunk boundary (64K chars) must still be found.
public class SourceTextContainsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(65_536 - 3)]     // "ContentHash" straddles the first chunk boundary
    [InlineData(65_536)]
    [InlineData(200_000)]        // past several chunks
    public void FindsBothNeedles_AnywhereInTheText(int at)
    {
        var body = new string(' ', at) + "ContentHash" + new string(' ', 70_000) + "record";
        Assert.True(RoslynCSharpAnalyzer.ContainsBoth(SourceText.From(body), "ContentHash", "record"));
    }

    [Fact]
    public void NeedsBoth()
    {
        var text = SourceText.From(new string('x', 150_000) + "ContentHash");
        Assert.False(RoslynCSharpAnalyzer.ContainsBoth(text, "ContentHash", "record"));
        Assert.False(RoslynCSharpAnalyzer.ContainsBoth(SourceText.From(""), "ContentHash", "record"));
    }
}
