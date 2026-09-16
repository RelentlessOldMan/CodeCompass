using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

// Exercises the network verify path (bounded-parallel candidate reads) by forcing network treatment on
// a local temp repo - no real share needed. Shares the process-global CODECOMPASS_FORCE_NETWORK env, so
// it lives in the serialized env collection (parallel classes would race on it).
[Collection("compaction-env")]
public class SegmentedIndexNetworkTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-idxnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static SegmentedIndex Build(TempRepo repo, string dir, long budget)
    {
        var idx = SegmentedIndex.Create(repo.Root, dir, budget);
        foreach (var (rel, full) in repo.Docs())
            idx.AddDocumentText(rel, File.ReadAllText(full));
        idx.Flush();
        return idx;
    }

    private static List<(string, int, int, string)> Run(SegmentedIndex idx, string query, int max, bool caseSensitive) =>
        idx.Search(query, max, caseSensitive).Select(m => (m.Path, m.Line, m.Column, m.LineText)).ToList();

    [Fact]
    public void ParallelVerify_MatchesSerial_ExactlyAndInOrder()
    {
        using var repo = new TempRepo();
        // Many files, many candidates per query, several matches per file, mixed case - enough to span
        // multiple parallel windows and shake out any ordering/merge bug.
        for (int i = 0; i < 40; i++)
            repo.Write($"pkg{i % 5}/file{i:D2}.cs",
                $"class Widget{i} {{ void Run() {{ }} }}\n" +
                "// run run RUN Run\n" +
                $"int value{i} = {i};\nWidget helper = new Widget{i}();\n");
        repo.Write("notes.txt", "nothing interesting here\n");

        var dir = NewTempDir();
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            using var idx = Build(repo, dir, budget: 256); // tiny budget -> many segments
            Assert.True(idx.SegmentCount >= 2);

            var queries = new[] { "Widget", "Run", "run", "value", "class", "helper", "zzz-absent" };
            foreach (var q in queries)
            {
                foreach (var max in new[] { 5, 1_000_000 }) // small (truncation) + unbounded
                {
                    foreach (var ci in new[] { false, true })
                    {
                        Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "0"); // serial
                        var serial = Run(idx, q, max, ci);
                        Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1"); // parallel
                        var parallel = Run(idx, q, max, ci);

                        Assert.Equal(serial, parallel); // identical results AND order, path-independent
                        if (max >= 1_000_000 && q == "Widget" && !ci)
                            Assert.NotEmpty(serial); // sanity: the corpus really does produce candidates
                    }
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", old);
            Directory.Delete(dir, true);
        }
    }
}
