using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

public class TrigramIndexTests
{
    [Fact]
    public void FindsLiteral_WithCorrectLineAndColumn()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class Foo\n{\n    void Bar() { Baz(); }\n}\n");

        var index = TrigramIndex.Build(repo.Root, repo.Docs());
        var hits = index.Search("Bar");

        var hit = Assert.Single(hits);
        Assert.Equal("a.cs", hit.Path);
        Assert.Equal(3, hit.Line);
        Assert.Equal(10, hit.Column); // 1-based; "    void Bar" -> B is col 10
    }

    [Fact]
    public void ReturnsNothing_WhenQueryAbsent()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "the quick brown fox");

        var index = TrigramIndex.Build(repo.Root, repo.Docs());
        Assert.Empty(index.Search("elephant"));
    }

    [Fact]
    public void FindsAcrossMultipleFiles()
    {
        using var repo = new TempRepo();
        repo.Write("one.cs", "public void Connect() {}");
        repo.Write("dir/two.cs", "// call Connect here\n");
        repo.Write("three.cs", "nothing relevant");

        var index = TrigramIndex.Build(repo.Root, repo.Docs());
        var paths = index.Search("Connect").Select(m => m.Path).OrderBy(p => p).ToArray();

        Assert.Equal(new[] { "dir/two.cs", "one.cs" }, paths);
    }

    [Fact]
    public void SurvivesSaveAndLoad()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "int Answer = 42;");

        var built = TrigramIndex.Build(repo.Root, repo.Docs());

        using var ms = new MemoryStream();
        built.Save(ms);
        ms.Position = 0;
        var loaded = TrigramIndex.Load(ms);

        Assert.Equal(built.DocumentCount, loaded.DocumentCount);
        Assert.Equal(built.TrigramCount, loaded.TrigramCount);
        Assert.Single(loaded.Search("Answer"));
    }

    // The lexical oracle: the trigram index must return exactly what a brute-force
    // substring scan returns, over random content and random queries.
    [Fact]
    public void MatchesBruteForceOracle_OverRandomCorpus()
    {
        var rng = new Random(1234); // fixed seed => reproducible
        const string alphabet = "abcde \n"; // tiny alphabet forces trigram collisions and real hits

        using var repo = new TempRepo();
        var fileTexts = new Dictionary<string, string>();
        for (int i = 0; i < 40; i++)
        {
            var text = RandomText(rng, alphabet, rng.Next(0, 400));
            var rel = $"f{i}.txt";
            repo.Write(rel, text);
            fileTexts[rel] = text;
        }

        var index = TrigramIndex.Build(repo.Root, repo.Docs());

        for (int q = 0; q < 200; q++)
        {
            var query = RandomText(rng, alphabet, rng.Next(1, 6)).Replace("\n", "");
            if (query.Length == 0) continue;

            var expected = BruteForce(repo.Root, fileTexts, query);
            var actual = index.Search(query, maxResults: 1_000_000)
                              .Select(m => (m.Path, m.Line, m.Column))
                              .ToHashSet();

            Assert.True(expected.SetEquals(actual),
                $"mismatch for query '{query}': expected {expected.Count}, got {actual.Count}");
        }
    }

    private static string RandomText(Random rng, string alphabet, int len)
    {
        var chars = new char[len];
        for (int i = 0; i < len; i++) chars[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(chars);
    }

    // Independent reference implementation, mirroring Search's line/column semantics.
    private static HashSet<(string, int, int)> BruteForce(
        string root, Dictionary<string, string> files, string query)
    {
        var result = new HashSet<(string, int, int)>();
        foreach (var (rel, _) in files)
        {
            // read from disk exactly as Search does, to keep encoding identical
            var text = File.ReadAllText(Path.Combine(root, rel));
            int idx = 0;
            while ((idx = text.IndexOf(query, idx, StringComparison.Ordinal)) >= 0)
            {
                int line = 1, lineStart = 0;
                for (int k = 0; k < idx; k++)
                    if (text[k] == '\n') { line++; lineStart = k + 1; }
                result.Add((rel, line, idx - lineStart + 1));
                idx += query.Length;
            }
        }
        return result;
    }
}
