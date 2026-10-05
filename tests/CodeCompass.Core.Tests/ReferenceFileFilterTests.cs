using CodeCompass.Core.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// The single filter deciding which files find_references' lexical layer counts (shared by the MCP tool and CLI `refs`).
// Pinned directly so an edit to the extension list can't silently shrink reference coverage - or re-admit doc/build
// noise - without a test noticing.
public class ReferenceFileFilterTests
{
    [Theory]
    [InlineData("src/a.cs", true)]
    [InlineData("src/a.c", true)]
    [InlineData("inc/a.h", true)]
    [InlineData("tool.py", true)]
    [InlineData("board.cmm", true)]
    [InlineData("web/app.min.js", true)]   // minified JS is still code
    [InlineData("Makefile", true)]         // no extension: not provably non-code
    [InlineData("README.md", false)]
    [InlineData("data/table.csv", false)]
    [InlineData("tags.json", false)]
    [InlineData("build/out.lst", false)]   // a disassembly listing committed beside sources
    [InlineData("old.c.bak", false)]
    [InlineData("fw.HEX", false)]          // case-insensitive
    public void ClassifiesByExtension(string path, bool isCode) =>
        Assert.Equal(isCode, ReferenceFileFilter.IsCodeReference(path));
}
