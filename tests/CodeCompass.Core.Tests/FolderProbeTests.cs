using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// A path named like an ignored file type (.o, .dll) may be a folder named like one (src/Contoso.Lib), created or moved in,
// which must be walked. Telling them apart took a stat per path - for every .o a build's clean deleted, under the index
// write lock (on a network root a round trip each), stalling every query behind it.
public class FolderProbeTests
{
    [Fact]
    public void UpdatePaths_ACleansThousandsOfDeletedObjectFiles_AreJudgedWithoutAStatEach()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.c", "int alpha_local(void) { return 1; }");
        repo.Write("src/Lib/widget.cs", "namespace N { class WidgetThing { } }");
        var b = RepositoryIndexer.Build(repo.Root); b.Text.Dispose(); b.Symbols.Dispose();
        Directory.Move(repo.FullPath("src/Lib"), repo.FullPath("src/Contoso.Lib"));

        var paths = Enumerable.Range(0, 1000).Select(i => repo.FullPath($"src/f{i}.o")).ToList(); // a clean deleted these
        paths.Add(repo.FullPath("src/Lib"));
        paths.Add(repo.FullPath("src/Contoso.Lib"));
        FolderProbe.DiskCallsForTest = 0;
        var u = RepositoryIndexer.UpdatePaths(repo.Root, paths);
        using (u.Text) using (u.Symbols)
        {
            Assert.False(u.Stats.FullRebuild);
            Assert.Contains(u.Text.Search("WidgetThing"), h => h.Path.Replace('\\', '/').Contains("src/Contoso.Lib/widget.cs"));
        }
        Assert.InRange(FolderProbe.DiskCallsForTest, 1, 3);
    }

    [Fact]
    public void Folders_ManyInOneParent_FindsExactlyTheFolders_WithOneListing()
    {
        using var repo = new TempRepo();
        var paths = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 6; i++)
        {
            Directory.CreateDirectory(repo.FullPath($"src/Part{i}.Lib"));
            paths.Add(repo.FullPath($"src/Part{i}.Lib"));
        }
        repo.Write("src/real.o", "object code"); // a file, not a folder
        paths.Add(repo.FullPath("src/real.o"));
        for (int i = 0; i < 6; i++) paths.Add(repo.FullPath($"src/gone{i}.o"));

        FolderProbe.DiskCallsForTest = 0;
        var folders = FolderProbe.Folders(paths);
        Assert.Equal(paths.Where(p => p.EndsWith(".Lib")).OrderBy(p => p), folders.OrderBy(p => p));
        Assert.Equal(1, FolderProbe.DiskCallsForTest);
    }

    [Fact]
    public void Folders_AFewPaths_AreStatEach_AndAGoneParent_IsOneCall()
    {
        using var repo = new TempRepo();
        Directory.CreateDirectory(repo.FullPath("src/Contoso.Lib"));
        repo.Write("src/real.o", "object code");
        var gone = Enumerable.Range(0, 20).Select(i => repo.FullPath($"obj2/f{i}.o")); // the clean removed their folder too

        FolderProbe.DiskCallsForTest = 0;
        var folders = FolderProbe.Folders(new[] { repo.FullPath("src/Contoso.Lib"), repo.FullPath("src/real.o") }.Concat(gone));
        Assert.Equal(new[] { repo.FullPath("src/Contoso.Lib") }, folders);
        Assert.Equal(3, FolderProbe.DiskCallsForTest); // two stats, one failed listing
    }
}
