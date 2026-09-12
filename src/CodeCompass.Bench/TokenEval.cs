using System.Text;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;

namespace CodeCompass.Bench;

/// <summary>
/// Estimates the token cost of the core task CodeCompass exists to cheapen: "show me the definition of
/// X." Two ways, over the same sampled symbols:
///   CodeCompass: <c>find_definition</c> -> the location + the definition's line range, with the small
///                definition inlined (what the agent actually receives).
///   Baseline:    what an agent without CodeCompass does -> grep the name across the repo (all matching
///                lines) + read the WHOLE file that defines it.
/// Tokens are estimated as ~chars/4 (a rough code heuristic; the ratio is what matters and is stable).
/// This is a model, not a live-LLM measurement - it's meant to quantify the shape of the saving, and it
/// prints its assumptions so the number is honest.
/// </summary>
public static class TokenEval
{
    private const int SnippetMaxLines = 40; // mirror CodeCompassTools' inline-snippet cap

    public static void Run(string root, int sampleSize)
    {
        root = Path.GetFullPath(root);
        if (!RepositoryIndexer.TryLoad(root, out SegmentedIndex text, out SegmentedSymbolIndex symbols))
        {
            Console.Error.WriteLine("no index; building...");
            var b = RepositoryIndexer.Build(root);
            text = b.Text; symbols = b.Symbols;
        }

        using (text)
        using (symbols)
        {
            // Sample distinct-named symbols that have a real body range (skip zero-range/aliased names).
            var sample = symbols.Find("", max: sampleSize * 6)
                .Where(s => s.EndLine > s.Line)
                .GroupBy(s => s.Name).Select(g => g.First())
                .Take(sampleSize).ToList();
            if (sample.Count == 0) { Console.Error.WriteLine("no ranged symbols to sample - is the repo indexed?"); return; }

            long ccChars = 0, baseChars = 0;
            foreach (var s in sample)
            {
                // CodeCompass: the find_definition result the agent receives.
                var defs = symbols.FindByName(s.Name);
                var ccOut = new StringBuilder();
                foreach (var d in defs)
                    ccOut.AppendLine($"{d.RelativePath}:{d.Line}-{d.EndLine}:{d.Column}: {d.Kind} {d.Name}");
                if (defs.Count == 1) ccOut.Append(ReadSnippet(root, defs[0]));
                ccChars += ccOut.Length;

                // Baseline: grep the name (all matching lines, like `grep -rn`) + read the defining file.
                long grepChars = 0;
                foreach (var m in text.Search(s.Name, 1000))
                    grepChars += (long)m.Path.Length + m.LineText.Length + 16; // "path:line:col: line\n"
                long fileChars = 0;
                try { fileChars = new FileInfo(Path.Combine(root, s.RelativePath.Replace('/', Path.DirectorySeparatorChar))).Length; }
                catch { }
                baseChars += grepChars + fileChars;
            }

            long ccTok = ccChars / 4, baseTok = baseChars / 4;
            double ratio = ccTok > 0 ? (double)baseTok / ccTok : 0;
            double saved = ratio > 0 ? (1 - 1.0 / ratio) * 100 : 0;

            Console.WriteLine($"Token-savings eval: {root}");
            Console.WriteLine($"  tasks:  {sample.Count} x \"show the definition of X\" (sampled ranged symbols)");
            Console.WriteLine( "  model:  CodeCompass = find_definition (location + inlined definition range)");
            Console.WriteLine( "          baseline    = grep the name across the repo + read the whole defining file");
            Console.WriteLine( "  tokens estimated as ~chars/4 (ratio is the stable, meaningful number):");
            Console.WriteLine($"    CodeCompass : {ccTok,12:N0}  ({ccChars:N0} chars)");
            Console.WriteLine($"    grep + read : {baseTok,12:N0}  ({baseChars:N0} chars)");
            if (ratio > 0)
                Console.WriteLine($"  => the grep+read approach costs {ratio:F1}x the tokens for the same answer ({saved:F0}% saved).");
        }
    }

    private static string ReadSnippet(string root, Symbol s)
    {
        try
        {
            if (s.EndLine <= s.Line || s.EndLine - s.Line + 1 > SnippetMaxLines) return "";
            var full = Path.Combine(root, s.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > 8L * 1024 * 1024) return "";
            var sb = new StringBuilder();
            int n = 0;
            foreach (var line in File.ReadLines(full))
            {
                n++;
                if (n < s.Line) continue;
                if (n > s.EndLine) break;
                sb.Append(n).Append(": ").AppendLine(line);
            }
            return sb.ToString();
        }
        catch { return ""; }
    }
}
