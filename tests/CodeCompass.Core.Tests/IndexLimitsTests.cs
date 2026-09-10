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

    // Regression for the pathological-parse stall. tree-sitter parse cost is LINEAR in size, but the
    // constant varies ~70x by content (see ParseSweep): deeply nested C++ template angle-brackets are
    // the worst shape measured (~0.1 MB/s, so a multi-MB header is tens of seconds). As a .h this used
    // to stall indexing; with the symbol-size cap the file is skipped for symbols yet stays fully
    // text-searchable, so the build stays fast. If the cap regresses, the nested-template parse of a
    // multi-MB file blows the time bound.
    [Fact]
    public void PathologicalLargeHeader_IndexesWithoutHanging_AndStaysSearchable()
    {
        using var repo = new TempRepo();
        // Deeply nested templates: "A<A<A<...int...>>>" - the genuinely slow-to-parse shape (each level
        // is "A<" + a trailing ">"). ~2.4 MB, over the 1 MB symbol cap so extraction is skipped.
        const int depth = 800_000; // 800k * 3 chars ~= 2.4 MB
        var sb = new StringBuilder(depth * 3 + 64);
        sb.Append("A x = ");
        for (int i = 0; i < depth; i++) sb.Append("A<");
        sb.Append("int");
        for (int i = 0; i < depth; i++) sb.Append('>');
        sb.Append(";\n");
        const string marker = "ZQXUNIQUEMARKER42";
        sb.Append(marker).Append('\n');
        repo.WriteBytes("chipreg.h", Encoding.ASCII.GetBytes(sb.ToString())); // .h -> tree-sitter C++

        // Build on a background thread with a generous bound; if the cap regresses, the nested-template
        // parse (uncapped ~2.4 MB at ~0.1 MB/s ~= 25 s+) stalls and Join times out.
        Exception? failure = null;
        var t = new System.Threading.Thread(() =>
        {
            try { var b = RepositoryIndexer.Build(repo.Root); b.Text.Dispose(); b.Symbols.Dispose(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)),
            "indexing a pathological large header did not finish in 60s - the symbol-size cap has regressed");
        Assert.Null(failure);

        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        using (text)
        using (symbols)
            Assert.NotEmpty(text.Search(marker)); // still fully text-searchable despite skipped symbols
    }
}
