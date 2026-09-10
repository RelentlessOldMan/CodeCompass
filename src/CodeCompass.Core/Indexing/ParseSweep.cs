using System.Diagnostics;
using System.Text;
using CodeCompass.Core.Symbols;

namespace CodeCompass.Core.Indexing;

/// <summary>
/// A controlled tree-sitter parse-cost sweep: generate source of increasing size and time the parse,
/// for both <b>ordinary code</b> (dense but well-formed declarations) and the <b>pathological</b>
/// shape that historically hung indexing (high-entropy tokens in a C/C++ header). This pins where the
/// ~O(n^2) cost becomes painful independent of what any real repo happens to contain - the companion
/// to <see cref="SymbolProfiler"/>, which measures the real-file size distribution. Read-only.
/// </summary>
public sealed record SweepPoint(long Bytes, double ParseMs, int Symbols, bool TimedOut, double GrowthFactor);

public sealed record SweepSeries(string Extension, string Shape, IReadOnlyList<SweepPoint> Points);

public static class ParseSweep
{
    // Doubling sweep; the run stops a series early once a single parse crosses the time ceiling.
    private static readonly long[] Sizes =
    {
        64L * 1024, 128L * 1024, 256L * 1024, 512L * 1024,
        1L << 20, 2L << 20, 4L << 20, 8L << 20, 16L << 20,
    };

    // The content shapes we sweep. "code" is the well-formed baseline; the rest are candidate
    // pathologies - we sweep all of them so the data shows which shape actually goes quadratic,
    // rather than assuming. (Machine-generated headers can be any of these.)
    private static readonly string[] Shapes = { "code", "random-lines", "one-long-line", "nested", "expr-chain", "template-nest" };

    /// <param name="maxBytes">Skip sizes above this (keeps tests/quick runs cheap). Default: no limit.</param>
    public static IReadOnlyList<SweepSeries> Run(long ceilingMs = 30_000, string[]? extensions = null, long maxBytes = long.MaxValue)
    {
        extensions ??= new[] { ".h", ".cs" }; // .h = C/C++ path (the real-world suspect); .cs = a contrast
        using var extractor = new TreeSitterSymbolExtractor();
        var series = new List<SweepSeries>();

        foreach (var ext in extensions)
        {
            // Warm the grammar load so it isn't charged to the first (smallest) sweep point.
            try { extractor.ExtractUncapped("warm" + ext, "int x;"); } catch { /* best effort */ }

            foreach (var shape in Shapes)
            {
                var points = new List<SweepPoint>();
                double prevMs = 0;
                foreach (var size in Sizes)
                {
                    if (size > maxBytes) break;
                    string text = Generate(shape, ext, size);
                    var (ms, syms, timedOut) = TimeParse(extractor, "sweep" + ext, text, ceilingMs);
                    double growth = prevMs > 0 ? ms / prevMs : 0; // ~2 => linear, ~4 => quadratic (size doubles each step)
                    points.Add(new SweepPoint(size, ms, syms, timedOut, growth));
                    prevMs = ms;
                    if (timedOut || ms >= ceilingMs) break; // no point going bigger
                }
                series.Add(new SweepSeries(ext, shape, points));
            }
        }
        return series;
    }

    private static string Generate(string shape, string ext, long size) => shape switch
    {
        "code" => GenerateCode(ext, size),
        "random-lines" => GeneratePathological(size, lineWidth: 80),
        "one-long-line" => GeneratePathological(size, lineWidth: int.MaxValue), // no newlines: stress the lexer
        "nested" => GenerateNested(size),
        "expr-chain" => GenerateExprChain(size),
        "template-nest" => GenerateTemplateNest(size),
        _ => GenerateCode(ext, size),
    };

    // Nested template angle brackets: A<A<A<...int...>>>. The C++ '<' is ambiguous (less-than vs
    // template), so deep nesting is the classic worst case for a C++ parser's disambiguation.
    private static string GenerateTemplateNest(long targetBytes)
    {
        var sb = new StringBuilder((int)Math.Min(targetBytes + 16, int.MaxValue));
        sb.Append("A x = ");
        long depth = (targetBytes - 16) / 3; // each level is "A<" + trailing ">"
        for (long i = 0; i < depth; i++) sb.Append("A<");
        sb.Append("int");
        for (long i = 0; i < depth; i++) sb.Append('>');
        sb.Append(";\n");
        return sb.ToString();
    }

    // Well-formed, symbol-rich declarations repeated to the target size. Parses linearly - the baseline.
    private static string GenerateCode(string ext, long targetBytes)
    {
        var sb = new StringBuilder((int)Math.Min(targetBytes + 4096, int.MaxValue));
        int i = 0;
        while (sb.Length < targetBytes)
        {
            switch (ext)
            {
                case ".cs":
                    sb.Append("class C").Append(i).Append(" { int F").Append(i)
                      .Append("; void M").Append(i).Append("(int a){ int x=a+").Append(i).Append("; } }\n");
                    break;
                case ".h":
                case ".cpp":
                    sb.Append("struct S").Append(i).Append(" { int x; int y; };\n")
                      .Append("int fn").Append(i).Append("(int a,int b){ return a+b+").Append(i).Append("; }\n");
                    break;
                default:
                    sb.Append("int v").Append(i).Append(" = ").Append(i).Append(";\n");
                    break;
            }
            i++;
        }
        return sb.ToString();
    }

    // High-entropy tokens (the shape the corpus generator uses). lineWidth controls how often a
    // newline is inserted; int.MaxValue means one enormous line, which stresses the lexer differently.
    private static string GeneratePathological(long targetBytes, int lineWidth)
    {
        const string charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_";
        var rnd = new Random(12345);
        var sb = new StringBuilder((int)Math.Min(targetBytes + 4096, int.MaxValue));
        int col = 0;
        while (sb.Length < targetBytes)
        {
            if (col >= lineWidth) { sb.Append('\n'); col = 0; }
            else { sb.Append(charset[rnd.Next(charset.Length)]); col++; }
        }
        return sb.ToString();
    }

    // Deeply nested delimiters: half a file of '(' then the matching ')'. Deep trees are a classic
    // trigger for super-linear parse/error-recovery cost.
    private static string GenerateNested(long targetBytes)
    {
        var sb = new StringBuilder((int)Math.Min(targetBytes + 16, int.MaxValue));
        sb.Append("int x = ");
        long open = (targetBytes - 16) / 2;
        for (long i = 0; i < open; i++) sb.Append('(');
        sb.Append('1');
        for (long i = 0; i < open; i++) sb.Append(')');
        sb.Append(";\n");
        return sb.ToString();
    }

    // One gigantic right-associative expression (x+x+x+...). Long flat sequences that the grammar
    // must fold can be quadratic depending on how the tree is built.
    private static string GenerateExprChain(long targetBytes)
    {
        var sb = new StringBuilder((int)Math.Min(targetBytes + 16, int.MaxValue));
        sb.Append("int x = 0");
        while (sb.Length < targetBytes) sb.Append("+a");
        sb.Append(";\n");
        return sb.ToString();
    }

    private static (double Ms, int Symbols, bool TimedOut) TimeParse(
        TreeSitterSymbolExtractor extractor, string rel, string text, long ceilingMs)
    {
        int symbols = 0;
        var sw = Stopwatch.StartNew();
        var t = new Thread(() =>
        {
            try { symbols = extractor.ExtractUncapped(rel, text).Count; } catch { symbols = -1; }
        }) { IsBackground = true };
        t.Start();
        bool finished = t.Join((int)Math.Min(ceilingMs, int.MaxValue));
        sw.Stop();
        return finished ? (sw.Elapsed.TotalMilliseconds, symbols, false) : (ceilingMs, -1, true);
    }
}
