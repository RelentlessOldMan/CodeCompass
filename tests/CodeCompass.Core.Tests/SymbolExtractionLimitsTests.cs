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
            var text = "namespace N { class Raised { void M() { } } }\n" + new string(' ', 1024 * 1024) + "\n"; // ~1 MB (padding is whitespace)
            Assert.NotEmpty(ex.Extract("raised.cs", text)); // now under the raised cap -> extracted
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", old); }
    }

    [Theory]
    [InlineData("{0x1A, 0x2B, 0x3C, 0xDD, 0xEF, 0xAB, 0xCD, 0x12, 0x34, 0x56}", true)]   // hex array
    [InlineData("1.2345, 6.7890, 11.2233, 44.5566, 77.8899, 0.0001, 9.9999", true)]      // decimal array
    [InlineData("int process_buffer(unsigned char* data, size_t length) { return 0; }", false)] // real code
    [InlineData("#define REG_STATUS 0x40001000\n#define REG_CONTROL 0x40001004", false)]  // register #defines
    public void IsLikelyNumericData_Classifies(string text, bool expected) =>
        Assert.Equal(expected, TreeSitterSymbolExtractor.IsLikelyNumericData(text));

    [Fact]
    public void Extract_LargeNumericDataBlob_SkippedEvenWhenCapRaised()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", "8"); // well above 1 MB
            using var ex = new TreeSitterSymbolExtractor();
            var sb = new System.Text.StringBuilder(1_300_000);
            sb.Append("int real_function(int a) { return a; }\n");         // a decoy that WOULD yield a symbol
            sb.Append("const unsigned char blob[] = {");
            while (sb.Length < 1_200_000) sb.Append("0x1A, 0x2B, 0x3C, 0xDD, "); // >1 MB of hex data
            sb.Append("};\n");
            // Over 1 MB and overwhelmingly numeric -> skipped by the data-blob guard despite the raised cap.
            Assert.Empty(ex.Extract("blob.c", sb.ToString()));
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", old); }
    }

    [Fact]
    public void Extract_LargeRealCode_StillExtractedWhenCapRaised()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", "8");
            using var ex = new TreeSitterSymbolExtractor();
            var sb = new System.Text.StringBuilder(1_300_000);
            int i = 0;
            while (sb.Length < 1_200_000) sb.Append("class Widget").Append(i++).Append(" { void Handle() {} }\n");
            // Over 1 MB but real code (letter-rich) -> NOT skipped; symbols still extracted.
            Assert.NotEmpty(ex.Extract("big.cs", sb.ToString()));
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", old); }
    }
}
