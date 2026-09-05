using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Text;
using CodeCompass.Core.Walking;

namespace CodeCompass.Bench;

public sealed record OracleResult(int Queries, int Mismatches, long SubsetBytes, int SubsetFiles, List<string> Examples);

/// <summary>
/// Correctness-at-scale: verifies the trigram index against a brute-force scan over
/// REAL repository content. Builds an in-memory index over a bounded slice of the repo
/// (so cost stays predictable), samples queries from the actual file text, and asserts
/// Search returns exactly what a naive substring scan finds. This is where real-world
/// edge cases (Unicode, CRLF, very long lines, huge files) get caught - the unit-test
/// oracle only sees synthetic content.
/// </summary>
public static class Verifier
{
    public static OracleResult LexicalOracle(string root, long budgetBytes, int queryCount, int seed = 12345)
    {
        root = Path.GetFullPath(root);
        var walker = new FileWalker(new IgnoreRules());

        var subset = new List<(string rel, string full)>();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        long acc = 0;

        foreach (var f in walker.Walk(root))
        {
            if (acc >= budgetBytes) break;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(f.FullPath); }
            catch { continue; }
            if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;

            texts[f.RelativePath] = TextDecoder.FromBytes(bytes);
            subset.Add((f.RelativePath, f.FullPath));
            acc += bytes.Length;
        }

        var index = TrigramIndex.Build(root, subset);
        var rng = new Random(seed);
        var queries = SampleQueries(texts, rng, queryCount);

        int mismatches = 0;
        var examples = new List<string>();
        const int cap = 1_000_000;
        int compared = 0;
        foreach (var q in queries)
        {
            var expected = BruteForce(texts, q);
            var actual = index.Search(q, cap)
                              .Select(m => (m.Path, m.Line, m.Column))
                              .ToHashSet();
            // A query that saturates the result cap isn't comparable; skip it.
            if (expected.Count >= cap || actual.Count >= cap) continue;
            compared++;
            if (!expected.SetEquals(actual))
            {
                mismatches++;
                if (examples.Count < 5)
                    examples.Add($"query '{Escape(q)}': brute-force {expected.Count}, index {actual.Count}");
            }
        }

        return new OracleResult(compared, mismatches, acc, subset.Count, examples);
    }

    private static List<string> SampleQueries(Dictionary<string, string> texts, Random rng, int count)
    {
        var queries = new List<string>();
        foreach (var token in new[] { "return", "class", "void", "for ", "if (", "public", "const", "error", "import" })
            if (texts.Values.Any(t => t.Contains(token, StringComparison.Ordinal)))
                queries.Add(token);

        var files = texts.Keys.ToList();
        int guard = 0;
        while (queries.Count < count && files.Count > 0 && guard++ < count * 20)
        {
            var text = texts[files[rng.Next(files.Count)]];
            int len = rng.Next(3, 13);
            if (text.Length <= len) continue;

            var sub = text.Substring(rng.Next(0, text.Length - len), len);
            int nl = sub.IndexOf('\n');
            if (nl >= 0) sub = sub.Substring(0, nl);
            sub = sub.TrimEnd('\r');
            // Skip degenerate all-whitespace/punctuation queries (they match everywhere).
            if (sub.Length >= 3 && sub.Any(char.IsLetterOrDigit)) queries.Add(sub);
        }
        return queries.Take(count).ToList();
    }

    // Mirrors TrigramIndex.ScanFile's line/column semantics exactly.
    private static HashSet<(string, int, int)> BruteForce(Dictionary<string, string> texts, string query)
    {
        var result = new HashSet<(string, int, int)>();
        foreach (var (rel, text) in texts)
        {
            int line = 1, lineStart = 0, scanned = 0, idx;
            while ((idx = text.IndexOf(query, scanned, StringComparison.Ordinal)) >= 0)
            {
                for (int k = scanned; k < idx; k++)
                    if (text[k] == '\n') { line++; lineStart = k + 1; }
                result.Add((rel, line, idx - lineStart + 1));
                scanned = idx + query.Length;
            }
        }
        return result;
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\t", "\\t");
}
