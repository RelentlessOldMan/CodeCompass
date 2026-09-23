using System;
using System.Linq;
using System.Threading.Tasks;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Walking;
using Xunit;

namespace CodeCompass.Core.Tests;

public class FileWalkerTests
{
    [Fact]
    public void ParallelWalk_MatchesSerialWalk_SameFilesSizesMtimes()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "x");
        repo.Write("src/b.cs", "yy");
        repo.Write("src/deep/c.cs", "zzz");
        repo.Write("bin/skip.cs", "x");            // ignored dir
        repo.Write("node_modules/x/d.js", "x");    // ignored dir
        for (int i = 0; i < 60; i++) repo.Write($"pkg{i % 8}/f{i}.cs", new string('x', i + 1));

        static (string, long, long)[] Run(string root, int threads) =>
            new FileWalker(new IgnoreRules(), threads).Walk(root)
                .Select(f => (f.RelativePath, f.Size, f.MTimeTicks))
                .OrderBy(t => t.Item1, StringComparer.Ordinal).ToArray();

        // The parallel walk must produce exactly the same set (order-independent) as the serial one.
        Assert.Equal(Run(repo.Root, 1), Run(repo.Root, 8));
    }

    [Fact]
    public async Task ParallelWalk_EarlyBreak_DoesNotHang()
    {
        using var repo = new TempRepo();
        for (int i = 0; i < 300; i++) repo.Write($"d{i % 12}/f{i}.cs", "x");

        var walker = new FileWalker(new IgnoreRules(), walkThreads: 8);
        // Abandon the walk after the first record - the workers must be cancelled cleanly, not deadlock
        // on a full output buffer or leak threads.
        var work = Task.Run(() => { foreach (var _ in walker.Walk(repo.Root)) break; });
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(work, finished); // completed before the timeout => no hang
        await work;                  // observe any fault
    }

    private static System.Collections.Generic.List<string> WalkRel(string root) =>
        new FileWalker(new IgnoreRules()).Walk(root).Select(f => f.RelativePath).OrderBy(p => p).ToList();

    [Fact]
    public void PrunesIgnoredDirectories()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "x");
        repo.Write("bin/b.cs", "x");
        repo.Write("node_modules/dep/c.js", "x");
        repo.Write(".git/config", "x");
        repo.Write("obj/d.cs", "x");
        repo.Write(".claude/index/tags.json", "x"); // another tool's index cache - must not be walked

        Assert.Equal(new[] { "src/a.cs" }, WalkRel(repo.Root));
    }

    [Fact]
    public void CarriesSizeAndMTimeFromEnumeration()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "hello world\n");
        var full = repo.FullPath("src/a.cs");

        var rec = new FileWalker(new IgnoreRules()).Walk(repo.Root).Single(f => f.RelativePath == "src/a.cs");

        // Size and mtime must match a direct stat - the walker reads them off the enumeration instead of
        // re-stat'ing, so this guards that the piggybacked metadata is correct (not just present).
        Assert.Equal(new System.IO.FileInfo(full).Length, rec.Size);
        Assert.Equal(System.IO.File.GetLastWriteTimeUtc(full).Ticks, rec.MTimeTicks);
        Assert.True(rec.MTimeTicks > 0);
    }

    [Fact]
    public void NormalizesRelativePathsWithForwardSlashes()
    {
        using var repo = new TempRepo();
        repo.Write("deep/nested/dir/x.cs", "x");

        var rels = WalkRel(repo.Root);
        Assert.Contains("deep/nested/dir/x.cs", rels);
        Assert.DoesNotContain(rels, p => p.Contains('\\'));
    }

    [Fact]
    public void EmptyRepo_ReturnsNothing()
    {
        using var repo = new TempRepo();
        Assert.Empty(WalkRel(repo.Root));
    }
}
