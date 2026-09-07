using CodeCompass.Core.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

public class WordBoundaryTests
{
    [Theory]
    [InlineData("call Foo();", 5, 3, true)]   // "Foo" delimited by space and '('
    [InlineData("callFoo", 4, 3, false)]      // letter immediately before
    [InlineData("Foo_bar", 0, 3, false)]      // underscore immediately after
    [InlineData("Foo1", 0, 3, false)]         // digit immediately after
    [InlineData("(Foo)", 1, 3, true)]         // parens around
    [InlineData("Foo", 0, 3, true)]           // whole line is the word
    [InlineData("x=Foo", 2, 3, true)]         // '=' before, end after
    public void IsWholeWord_Cases(string line, int start, int length, bool expected)
    {
        Assert.Equal(expected, WordBoundary.IsWholeWord(line, start, length));
    }
}
