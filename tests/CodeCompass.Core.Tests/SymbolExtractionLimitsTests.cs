using System;
using CodeCompass.Core.Symbols;
using Xunit;

namespace CodeCompass.Core.Tests;

// tree-sitter parse cost is linear in size but the constant varies ~70x by content (see ParseSweep):
// on the worst shape (deeply nested C++ templates) a single multi-MB generated header parses for tens
// of seconds and stalls the index. Symbol extraction is skipped above the cap (the file is still
// trigram-indexed and searchable).
// Serialized with IndexLimitsTests (both drive the process-global CODECOMPASS_MAX_SYMBOL_MB env).
[Collection("symbolcap-env")]
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
