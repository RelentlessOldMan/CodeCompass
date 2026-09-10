using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Walking;
using Xunit;

namespace CodeCompass.Core.Tests;

// Escape hatches + visibility for the "dense generated headers" pathology: an operator can lower
// the size cap or exclude a directory without editing source, and over-cap skips are counted
// (not silent).
// Shares CODECOMPASS_MAX_SYMBOL_MB with SymbolExtractionLimitsTests, so the collection serializes
// the two classes (the pathological-header test relies on the default cap being in effect).
[Collection("symbolcap-env")]
public class IndexLimitsTests
{
    [Fact]
    public void IgnoreRules_ReadsMaxFileMbFromEnv()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", "2");
            Assert.Equal(2L * 1024 * 1024, new IgnoreRules().MaxFileSizeBytes);
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", old); }
    }

    [Fact]
    public void IgnoreRules_ReadsIgnoreDirsFromEnv()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_IGNORE");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_IGNORE", "chipreg; generated");
            var r = new IgnoreRules();
            Assert.True(r.IsIgnoredDirectory("chipreg"));
            Assert.True(r.IsIgnoredDirectory("generated"));
            Assert.False(r.IsIgnoredDirectory("src"));
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_IGNORE", old); }
    }

    [Fact]
    public void Walker_ExcludesAndCountsOverCapFiles()
    {
        using var repo = new TempRepo();
        repo.WriteBytes("small.cs", new byte[100]);
        repo.WriteBytes("huge.cs", new byte[4096]);

        var walker = new FileWalker(new IgnoreRules(maxFileSizeBytes: 1024)); // 1 KB cap
        var rels = walker.Walk(repo.Root).Select(f => f.RelativePath).ToList();

        Assert.Contains("small.cs", rels);
        Assert.DoesNotContain("huge.cs", rels); // over the cap -> excluded
        Assert.Equal(1, walker.OverCapSkipped);
        Assert.EndsWith("huge.cs", walker.LargestOverCapPath);
        Assert.Equal(4096, walker.LargestOverCapBytes);
    }

    // Regression for the tree-sitter O(n^2) hang: a large, high-vocabulary generated header used to
    // stall indexing indefinitely (parsed as C++). With the symbol-size cap it must index quickly
    // and stay text-searchable. Pre-fix this file (~2.5 MB) took ~2 minutes; post-fix well under 1 s.
    [Fact]
    public void PathologicalLargeHeader_IndexesWithoutHanging_AndStaysSearchable()
    {
        using var repo = new TempRepo();
        const string charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_ ";
        var rnd = new Random(20260909);
        var sb = new StringBuilder(2_600_000);
        for (int i = 0; i < 2_500_000; i++)
        {
            sb.Append(charset[rnd.Next(charset.Length)]);
            if (i % 80 == 79) sb.Append('\n');
        }
        const string marker = "ZQXUNIQUEMARKER42";
        sb.Append('\n').Append(marker).Append('\n');
        repo.WriteBytes("chipreg.h", Encoding.ASCII.GetBytes(sb.ToString())); // .h -> tree-sitter C++

        // Build on a worker with a generous bound; if the hang regresses, this times out and fails.
        var build = Task.Run(() =>
        {
            var b = RepositoryIndexer.Build(repo.Root);
            b.Text.Dispose();
            b.Symbols.Dispose();
        });
        Assert.True(build.Wait(TimeSpan.FromSeconds(60)),
            "indexing a pathological large header did not finish in 60s - the tree-sitter O(n^2) hang has regressed");

        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        using (text)
        using (symbols)
            Assert.NotEmpty(text.Search(marker)); // still fully text-searchable despite skipped symbols
    }
}
