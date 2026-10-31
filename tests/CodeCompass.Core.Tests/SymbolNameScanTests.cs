using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

// search_symbols scans a segment's name bytes in chunks instead of decoding each name. It must pick exactly the
// names the old per-name test picked (decode to UTF-16, then Contains OrdinalIgnoreCase), in index order.
public class SymbolNameScanTests
{
    private static readonly string[] Pieces =
    {
        "Foo", "foo", "BAR", "bar", "Get", "Declared", "Symbol", "x", "X", "_", "1", "Async", "Ab", "aB",
        "é", "É", "ſ", "s", "S", "ı", "I", "i", "K", "k", "k", "ß", "Σ", "σ", "ς", "日本", "😀", "𐐀", "𐐨", "K", "\uD800", "ǅ",
    };

    [Fact]
    public void NamesContaining_MatchesTheDecodingCompare_ForEveryQuery()
    {
        var rng = new Random(20261021);
        var names = new List<string>();
        for (int i = 0; i < 30_000; i++)
        {
            var sb = new StringBuilder();
            int parts = rng.Next(0, 5);
            for (int p = 0; p < parts; p++) sb.Append(Pieces[rng.Next(Pieces.Length)]);
            names.Add(sb.ToString());
        }
        names.Add(new string('a', 200_000) + "Needle"); // longer than any scan chunk
        names.Add("ZzLast");

        var file = Path.Combine(Path.GetTempPath(), "cc-namescan-" + Guid.NewGuid().ToString("N") + ".ccsym");
        try
        {
            var b = new SymbolSegmentBuilder();
            foreach (var n in names) b.Add(new Symbol(n, SymbolKind.Method, "a.cs", 1, 1));
            b.WriteTo(file);
            using var r = new SymbolSegmentReader(file);
            var decoded = Enumerable.Range(0, r.Count).Select(r.GetName).ToArray();

            var queries = new List<string> { "", "a", "A", "needle", "NEEDLE", "aaaN", "zzlast", "ZzLast", "s", "S", "ſ", "i", "I",
                "ı", "k", "K", "k", "é", "É", "ß", "SS", "σ", "Σ", "ς", "日本", "😀", "�", "ǅ", "ǆ", "Ǆ", "ooB", "rG", "QQQ", "_1" };
            // Spans that cross from one name into the next: the old per-name test never matched these.
            for (int k = 0; k < 200; k++)
            {
                int i = rng.Next(decoded.Length - 1);
                var joined = decoded[i] + decoded[i + 1];
                if (joined.Length < 2) continue;
                int s = rng.Next(joined.Length - 1), len = rng.Next(1, Math.Min(8, joined.Length - s) + 1);
                queries.Add(joined.Substring(s, len));
            }
            for (int k = 0; k < 200; k++)
            {
                var n = decoded[rng.Next(decoded.Length)];
                if (n.Length == 0 || n.Length > 64) continue;
                int s = rng.Next(n.Length), len = rng.Next(1, n.Length - s + 1);
                var q = n.Substring(s, len);
                queries.Add(rng.Next(2) == 0 ? q.ToUpperInvariant() : q.ToLowerInvariant());
            }

            foreach (var q in queries)
            {
                var expected = Enumerable.Range(0, decoded.Length).Where(i => decoded[i].Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                var actual = r.NamesContaining(q).ToList();
                Assert.True(expected.SequenceEqual(actual), $"query '{q}': expected {expected.Count} names, got {actual.Count}");
            }
        }
        finally { File.Delete(file); }
    }

    // The scan is exact only because no non-ASCII char (or surrogate pair) equals an ASCII char under OrdinalIgnoreCase
    // - true of .NET's simple case mapping (no expansions like ß -> SS, no ſ -> s). Should a runtime ever fold one in, this
    // fails, and the scan needs a per-name check for names with non-ASCII bytes.
    [Fact]
    public void NoNonAsciiChar_EqualsAnAsciiChar_IgnoringCase()
    {
        for (int a = 0; a < 0x80; a++)
        {
            var ascii = ((char)a).ToString();
            for (int c = 0x80; c <= 0xFFFF; c++)
                Assert.False(((char)c).ToString().Equals(ascii, StringComparison.OrdinalIgnoreCase), $"U+{c:X4} ~ U+{a:X4}");
            for (int cp = 0x10000; cp <= 0x10FFFF; cp += 0x3F) // a spread of supplementary chars, as surrogate pairs
                Assert.DoesNotContain(ascii, char.ConvertFromUtf32(cp), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NamesContaining_StopsReadingWhenTheCallerStops()
    {
        var file = Path.Combine(Path.GetTempPath(), "cc-namescan-" + Guid.NewGuid().ToString("N") + ".ccsym");
        try
        {
            var b = new SymbolSegmentBuilder();
            for (int i = 0; i < 1000; i++) b.Add(new Symbol($"Name{i:D4}", SymbolKind.Method, "a.cs", 1, 1));
            b.WriteTo(file);
            using var r = new SymbolSegmentReader(file);
            Assert.Equal(new[] { 0, 1, 2 }, r.NamesContaining("name").Take(3).ToArray());
        }
        finally { File.Delete(file); }
    }
}
