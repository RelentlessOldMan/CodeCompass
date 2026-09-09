using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Hardening against a corrupt or tampered on-disk cache (the shared-machine / disk-corruption
// threat from the security review): never read/return files outside the repo, never open files
// outside the cache dir, and degrade to a rebuild rather than crash on structural corruption.
public class SecurityTests
{
    [Theory]
    [InlineData("src/a.cs", true)]
    [InlineData("a/b/c.cs", true)]
    [InlineData("../escape.cs", false)]
    [InlineData("a/../../x", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("", false)]
    public void IsInsideRepo(string rel, bool expected) =>
        Assert.Equal(expected, PathSafety.IsInsideRepo(rel));

    [Theory]
    [InlineData("seg-00000001.ccseg", true)]
    [InlineData("a/b.ccseg", false)]
    [InlineData("../b.ccseg", false)]
    [InlineData("", false)]
    public void IsBareFileName(string name, bool expected) =>
        Assert.Equal(expected, PathSafety.IsBareFileName(name));

    [Fact]
    public void TamperedManifest_TraversalEntryIsIgnored()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class A { } }");
        var b = RepositoryIndexer.Build(repo.Root);
        int docs = b.Text.DocumentCount;
        b.Text.Dispose(); b.Symbols.Dispose();

        // Inject a path-traversal segment entry into the manifest.
        var manifest = Path.Combine(IndexStore.GetCacheDir(repo.Root), "segments.manifest");
        var lines = File.ReadAllLines(manifest).ToList();
        lines.Add(@"..\..\..\Windows\System32\evil.ccseg");
        File.WriteAllLines(manifest, lines);

        // Loading must ignore the malicious entry (not try to open outside the cache dir) and
        // still serve the real segments.
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        using (text)
        using (symbols)
            Assert.Equal(docs, text.DocumentCount);
    }

    [Fact]
    public void CorruptSegmentFile_DegradesToRebuild_NoCrash()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Findable { } }");
        var b = RepositoryIndexer.Build(repo.Root);
        b.Text.Dispose(); b.Symbols.Dispose();

        // Corrupt a segment file on disk.
        var seg = Directory.GetFiles(IndexStore.GetCacheDir(repo.Root), "seg-*.ccseg").First();
        File.WriteAllBytes(seg, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });

        // TryLoad must fail cleanly (so the caller rebuilds) rather than throw/crash...
        Assert.False(RepositoryIndexer.TryLoad(repo.Root, out _, out _));

        // ...and a fresh build recovers.
        var rebuilt = RepositoryIndexer.Build(repo.Root);
        using (rebuilt.Text)
        using (rebuilt.Symbols)
            Assert.NotEmpty(rebuilt.Text.Search("Findable"));
    }
}
