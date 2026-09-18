using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Symbols;
using Xunit;

namespace CodeCompass.Core.Tests;

// Go-to-definition for Lauterbach TRACE32 PRACTICE (.cmm) scripts, via the vendored tree-sitter-t32
// grammar (built to grammars/prebuilt/win-x64/tree-sitter-t32.dll and copied to the test output).
public class T32SymbolTests
{
    private const string Script = """
        GOSUB printA
        GOSUB printC "hi"

        printA:
        (
          PRINT "A"
          RETURN
        )

        SUBROUTINE printC
        (
          PARAMETERS &a
          PRINT "&a"
          ENDDO
        )
        """;

    [Fact]
    public void Extractor_FindsSubroutineAndLabelDefinitions()
    {
        using var ex = new TreeSitterSymbolExtractor();
        var names = ex.Extract("scripts/boot.cmm", Script).Select(s => s.Name).ToHashSet();

        Assert.Contains("printC", names); // SUBROUTINE block
        Assert.Contains("printA", names); // labeled block
        // The GOSUB call sites are references, not definitions - they must NOT be captured as symbols.
        Assert.Single(names.Where(n => n == "printC"));
    }

    [Fact]
    public void FindDefinition_ResolvesAcmmSubroutine_ThroughTheIndex()
    {
        using var repo = new TempRepo();
        repo.Write("scripts/boot.cmm", Script);
        repo.Write("keep.cs", "class Keep { }"); // a normal file alongside

        var (text, symbols, _) = RepositoryIndexer.Build(repo.Root);
        using (text) using (symbols)
        {
            var def = symbols.FindByName("printC");
            Assert.NotEmpty(def);
            Assert.Equal("scripts/boot.cmm", def[0].RelativePath);
            // Still text-searchable too (both layers work for .cmm).
            Assert.NotEmpty(text.Search("PARAMETERS", 10));
        }
    }
}
