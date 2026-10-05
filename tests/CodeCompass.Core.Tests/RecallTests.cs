using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Mcp;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// Review WP-G: deterministic false negatives ("lexical is the floor" broken) and silently-incomplete answers.
public class RecallTests
{
    private static void BuildIndex(TempRepo repo)
    {
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
    }

    // P1-15: a single line over 16 MB is cut mid-line; a token straddling that cut had trigrams on both sides, so
    // neither block's Bloom admitted it, the block scan couldn't span the cut, and the file reported "handled" - a
    // deterministic zero for text that is right there.
    [Fact]
    public void TokenStraddlingAForcedBlockCut_IsFound()
    {
        using var repo = new TempRepo();
        const int cut = 16 << 20; // the hard cap: a line with no newline is cut here
        var sb = new StringBuilder(cut + 4096);
        sb.Append('x', cut - 5).Append("NeedleSpan").Append('y', 2000).Append('\n').Append("tail line\n");
        repo.Write("bundle.js", sb.ToString());
        BuildIndex(repo);
        Assert.True(PositionalSidecar.HasSidecar(IndexStore.GetCacheDir(repo.Root), "bundle.js"));

        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
        using (t) using (s) Assert.Single(t.Search("NeedleSpan"));
    }

    // P2-8: newlines INSIDE a multi-line match weren't counted, so each later match reported a line too small.
    [Fact]
    public void MultiLineQuery_LaterMatchesReportTheRightLine()
    {
        var results = new System.Collections.Generic.List<SearchMatch>();
        FileScanner.ScanText("a.txt", "foo\nbar\nfoo\nbar\n", "foo\nbar", results, 10);
        Assert.Equal(new[] { 1, 3 }, results.Select(r => r.Line));
    }

    // P2-5: line-aligned blocks put every block-crossing trigram (it contains '\n') in neither Bloom, so a multi-line
    // query whose match crosses a block boundary was a silent miss in any block-indexed (>= 8 MB) file.
    [Fact]
    public void MultiLineQuery_AcrossABlockBoundary_IsFound()
    {
        using var repo = new TempRepo();
        var sb = new StringBuilder();
        // 100-byte lines: the first ~1 MB block ends after line 10485, so "alpha" ends block 1 and "beta" starts block 2.
        for (int i = 1; i <= 100_000; i++)
            sb.Append(i == 10485 ? new string('x', 94) + "alpha" : i == 10486 ? "beta" + new string('y', 95) : new string('z', 99)).Append('\n');
        repo.Write("data.txt", sb.ToString());
        BuildIndex(repo);
        Assert.True(PositionalSidecar.HasSidecar(IndexStore.GetCacheDir(repo.Root), "data.txt"));
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
        using (t) using (s)
        {
            var hit = Assert.Single(t.Search("alpha\nbeta"));
            Assert.Equal(10485, hit.Line);
        }
    }

    // P2-11: a short read in the block scan (the file changed since indexing) returned "handled" with no hits, so the
    // caller never fell back to a whole-file scan.
    [Fact]
    public void BlockScanReadFailure_FallsBackToAWholeFileScan()
    {
        using var repo = new TempRepo();
        var sb = new StringBuilder();
        for (int i = 0; i < 100_000; i++) sb.Append(new string('q', 99)).Append('\n');
        sb.Append("LastBlockToken here\n").Append(new string('w', 500)).Append('\n');
        repo.Write("big.txt", sb.ToString());
        BuildIndex(repo);
        Assert.True(PositionalSidecar.HasSidecar(IndexStore.GetCacheDir(repo.Root), "big.txt"));

        using (var fs = new FileStream(repo.FullPath("big.txt"), FileMode.Open)) fs.SetLength(fs.Length - 200); // token survives
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
        using (t) using (s) Assert.Single(t.Search("LastBlockToken"));
    }

    // P2-9: a binary-sniffed file got no ledger entry, so every update re-read it in full just to re-discover it.
    [Fact]
    public void BinaryFile_IsRecordedInTheLedger()
    {
        using var repo = new TempRepo();
        repo.WriteBytes("blob.cs", new byte[] { 0x63, 0x6C, 0x00, 0x01, 0x02, 0x41 });
        repo.Write("a.cs", "class A { }");
        BuildIndex(repo);
        using var snap = RepositoryIndexer.LoadSnapshot(repo.Root);
        Assert.True(snap.TryGetValue("blob.cs", out var st));
        Assert.True(st.IsBinary);

        var u = RepositoryIndexer.Update(repo.Root);
        u.Text.Dispose(); u.Symbols.Dispose();
        Assert.Equal(0, u.Stats.Added + u.Stats.Modified + u.Stats.Removed); // unchanged binary: not a phantom add
    }

    // P2-12: same-size edit within the same timestamp tick right after the ledger was written: size+mtime matched and the
    // prefilter skipped the file forever ("racily clean").
    [Fact]
    public void RacilyCleanEdit_IsPickedUpByUpdate()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class AlphaOne { }");
        BuildIndex(repo);
        var mtime = File.GetLastWriteTimeUtc(repo.FullPath("a.cs"));
        repo.Write("a.cs", "class AlphaTwo { }");                  // same length
        File.SetLastWriteTimeUtc(repo.FullPath("a.cs"), mtime);     // a coarse clock: the same timestamp
        var u = RepositoryIndexer.Update(repo.Root);
        using (u.Text) using (u.Symbols)
        {
            Assert.NotEmpty(u.Symbols.FindByName("AlphaTwo"));
            Assert.Empty(u.Symbols.FindByName("AlphaOne"));
        }
    }

    // P2-14: case-insensitive candidate selection expanded each char to {lower, upper} only, but the verify step uses
    // OrdinalIgnoreCase - so the micro sign never admitted a file written with Greek mu (both uppercase to U+039C).
    [Fact]
    public void CaseInsensitiveSearch_UsesTheSameFoldingAsTheVerify()
    {
        using var repo = new TempRepo();
        string greekMu = ((char)0x03BC).ToString(), micro = ((char)0x00B5).ToString();
        repo.Write("t.txt", "latency " + greekMu + "sec budget\n");
        BuildIndex(repo);
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
        using (t) using (s) Assert.Single(t.Search(micro + "sec", caseSensitive: false));
    }

    // P1-16: default "build output" names matched at any depth, so a monorepo's packages/ sources were invisible -
    // with no disclosure and no way back in.
    [Fact]
    public void PackagesDir_IsDisclosedOnAZero_AndKeepDirsIndexesIt()
    {
        using var repo = new TempRepo();
        repo.Write("packages/ui/src/Button.ts", "export class InPackagesButton { }");
        repo.Write("root.ts", "export const x = 1;");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            var miss = CodeCompassTools.SearchCode("InPackagesButton");
            Assert.Contains("keepDirs", miss);

            repo.Write(".codecompass.json", "{ \"keepDirs\": [\"packages\"] }");
            ServerContext.Init(repo.Root);
            CodeCompassTools.Reindex();
            Assert.Contains("Button.ts", CodeCompassTools.SearchCode("InPackagesButton"));
        }
        finally
        {
            File.Delete(repo.FullPath(".codecompass.json"));
            CodeCompass.Core.Config.CodeCompassConfig.Load(repo.Root);
            ServerContext.Init(repo.Root);
        }
    }

    // P1-18: find_definition had no cap - a common name across a federated workspace dumped every definition.
    [Fact]
    public void FindDefinition_IsCapped_WithAMoreExistFooter()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 60; i++) repo.Write($"n{i:D2}/Dup.cs", $"namespace N{i} {{ class Dup {{ }} }}");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            var r = CodeCompassTools.FindDefinition("Dup");
            Assert.Contains("MORE EXIST", r);
            Assert.Equal(50, r.Split('\n').Count(l => l.Contains("Dup.cs")));
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // P2-2: the reference backfill's raw-hit budget could run out on non-references (docs) before reaching a real one,
    // and the answer came back as a confident "No references found".
    [Fact]
    public void LexicalBudgetExhaustion_IsDisclosed_EvenOnAZero()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 20; i++) repo.Write($"a{i:D2}.md", "zork_fn is documented here\n");
        repo.Write("z.py", "zork_fn()\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            var r = CodeCompassTools.FindReferences("zork_fn", maxResults: 1);
            Assert.Contains("lexical scan reached its budget", r);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // P2-6: the comment/string filter judged the INDEX's coordinates against the CURRENT file. After an edit those
    // coordinates can land inside a comment that wasn't there - dropping a real reference. Fail open when the token is
    // no longer at that position.
    [Fact]
    public void SpanFilter_KeepsAHitWhoseCoordinatesAreStale()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "// Ping in a comment\nclass C { void M() { } }\n");
        var path = repo.FullPath("a.cs");
        var f = new LexicalSpanFilter("Ping");
        Assert.True(f.IsInCommentOrString(path, 1, 4));    // the token really is in the comment there
        Assert.False(f.IsInCommentOrString(path, 1, 10));  // a stale position inside the comment: keep the hit
    }

    // P2-10: a request for (nearly) the whole budget waited for a full drain while small requests kept winning.
    [Fact]
    public async Task ByteBudget_ServesRequestsInArrivalOrder()
    {
        var b = new ByteBudget(100);
        b.Acquire(60);
        using var bigIn = new ManualResetEventSlim(false);
        using var smallIn = new ManualResetEventSlim(false);
        var big = Task.Run(() => { b.Acquire(100); bigIn.Set(); });
        Thread.Sleep(200);                                   // the big request is queued first
        var small = Task.Run(() => { b.Acquire(10); smallIn.Set(); });
        Assert.False(smallIn.Wait(300));                     // 40 bytes are free, but the small one must not jump the line
        b.Release(60);
        Assert.True(bigIn.Wait(5000));
        Assert.False(smallIn.IsSet);
        b.Release(100);
        Assert.True(smallIn.Wait(5000));
        await Task.WhenAll(big, small);
    }
}
