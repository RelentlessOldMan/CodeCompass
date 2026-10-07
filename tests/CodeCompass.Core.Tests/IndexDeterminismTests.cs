using System;
using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// Field report (generated-header rounds): two builds of the SAME tree reported different trigram-postings totals - the
// count depended on how files were spread across worker threads. A build's output must depend only on the tree: same
// stats and same answers whatever the parallelism. Includes a file past the large-file threshold so the block-indexed
// path (counted separately) is covered too.
public class IndexDeterminismTests
{
    [Fact]
    public void Build_SameTree_SingleVsManyThreads_SameStatsAndAnswers()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 40; i++)
        {
            repo.Write($"src/m{i}.c", $"#include \"m{i}.h\"\nint fn_{i}(int x) {{ return x * {i} + helper_{i % 7}(x); }}\n");
            repo.Write($"src/m{i}.h", $"int fn_{i}(int x);\n#define LIMIT_{i} {i * 3}\n");
            repo.Write($"app/C{i}.cs", $"namespace App {{ public class C{i} {{ public int Run() => {i}; }} }}\n");
        }
        var big = new StringBuilder();
        for (int i = 0; big.Length < (int)LargeFileIndexer.SidecarThresholdBytes + (1 << 20); i++)
            big.Append("static const unsigned reg_").Append(i).Append(" = 0x").Append(i.ToString("x8")).Append("; /* fn_").Append(i % 40).Append(" */\n");
        repo.Write("gen/regs_big.h", big.ToString());

        var single = BuildWith(repo.Root, threads: 1);
        var many = BuildWith(repo.Root, threads: 8);

        Assert.Equal(single.Stats.Files, many.Stats.Files);
        Assert.Equal(single.Stats.Bytes, many.Stats.Bytes);
        Assert.Equal(single.Stats.TrigramPostings, many.Stats.TrigramPostings);
        Assert.Equal(single.Stats.Symbols, many.Stats.Symbols);
        Assert.Equal(single.Answers, many.Answers);
    }

    private static (IndexStats Stats, string Answers) BuildWith(string root, int threads)
    {
        var prior = Environment.GetEnvironmentVariable("CODECOMPASS_THREADS");
        Environment.SetEnvironmentVariable("CODECOMPASS_THREADS", threads.ToString());
        try
        {
            var (text, symbols, stats) = RepositoryIndexer.Build(root);
            using (text) using (symbols)
            {
                var answers = new StringBuilder();
                foreach (var q in new[] { "fn_7", "helper_3", "LIMIT_12", "reg_4000", "Run()" })
                    foreach (var m in text.Search(q, 10_000, orderByPath: true))
                        answers.Append(m.Path).Append(':').Append(m.Line).Append(':').Append(m.Column).Append('\n');
                foreach (var n in new[] { "fn_7", "C12", "Run" })
                    foreach (var s in symbols.FindByName(n).OrderBy(s => s.RelativePath, StringComparer.Ordinal).ThenBy(s => s.Line))
                        answers.Append(s.RelativePath).Append(':').Append(s.Line).Append(':').Append(s.Name).Append('\n');
                return (stats, answers.ToString());
            }
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_THREADS", prior); }
    }
}
