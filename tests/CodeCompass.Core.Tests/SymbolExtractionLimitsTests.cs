using System;
using CodeCompass.Core.Symbols;
using Xunit;

namespace CodeCompass.Core.Tests;

// tree-sitter parse cost is ~O(n^2) on pathological content; without a size cap a single huge
// generated header can hang the whole index. Symbol extraction is skipped above the cap (the file
// is still trigram-indexed and searchable).
public class SymbolExtractionLimitsTests
{
    [Fact]
    public void Extract_NormalFile_ReturnsSymbols()
    {
        using var ex = new TreeSitterSymbolExtractor();
        var syms = ex.Extract("a.cs", "namespace N { class Small { void M() { } } }");
        Assert.NotEmpty(syms);
    }

    [Fact]
    public void Extract_OverSymbolSizeCap_SkipsWithoutParsing()
    {
        using var ex = new TreeSitterSymbolExtractor(); // default cap 1 MB
        // Valid C# but well over the cap - must be skipped fast (no O(n^2) parse), returning nothing.
        var huge = "namespace N { class Big { } }\n" + new string(' ', 1024 * 1024) + "\n";
        Assert.True(huge.Length > 1024 * 1024);
        Assert.Empty(ex.Extract("big.cs", huge));
    }

    [Fact]
    public void Extract_RespectsMaxSymbolMbEnv()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", "4"); // raise cap to 4 MB
            using var ex = new TreeSitterSymbolExtractor();
            var text = "namespace N { class Raised { void M() { } } }\n" + new string(' ', 1024 * 1024) + "\n"; // ~1 MB
            Assert.NotEmpty(ex.Extract("raised.cs", text)); // now under the raised cap -> extracted
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", old); }
    }
}
