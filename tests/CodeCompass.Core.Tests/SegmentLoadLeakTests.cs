using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// A load that failed on a corrupt or newer-format segment left that segment - and every one opened before it - memory-
// mapped until a GC. Each retry of a held-back edit leaked more, and on Windows an open mapping blocks overwriting or
// deleting the file (a terminal `codecompass index` over it, say).
public class SegmentLoadLeakTests
{
    [Theory]
    [InlineData("seg-*.ccseg", false)]
    [InlineData("sym-*.ccsym", false)]
    [InlineData("seg-*.ccseg", true)]
    [InlineData("sym-*.ccsym", true)]
    public void AFailedLoad_LeavesNoSegmentMapped(string pattern, bool newerFormat)
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class AlphaLocal { } }");
        var b = RepositoryIndexer.Build(repo.Root); b.Text.Dispose(); b.Symbols.Dispose();
        repo.Write("b.cs", "namespace N { class BravoLocal { } }");
        var u = RepositoryIndexer.Update(repo.Root); u.Text.Dispose(); u.Symbols.Dispose();
        var cache = IndexStore.CacheDirPath(repo.Root);
        var segs = Directory.GetFiles(cache, pattern).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.True(segs.Count >= 2, "needs a segment opened before the bad one");

        using (var fs = new FileStream(segs[^1], FileMode.Open, FileAccess.Write))
        {
            if (newerFormat) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }
            else fs.Write(new byte[4]); // no magic
        }
        Assert.False(RepositoryIndexer.TryLoad(repo.Root, out _, out _, out var why));
        Assert.Equal(newerFormat ? IndexLoadFailure.NewerFormat : IndexLoadFailure.Corrupt, why);

        foreach (var f in Directory.GetFiles(cache, "seg-*.ccseg").Concat(Directory.GetFiles(cache, "sym-*.ccsym")))
            using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } // throws while still mapped
    }
}
