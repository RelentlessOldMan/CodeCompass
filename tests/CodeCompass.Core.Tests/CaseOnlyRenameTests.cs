using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// On a case-insensitive disk (NTFS, SMB, default macOS) the OLD spelling of a case-only rename still "exists", so the
// targeted update kept it beside the new one: search and def listed Widget.cs, widget.cs and WIDGET.cs for one file,
// until a full update. The index keys paths case-sensitively, so only the spelling on disk may stay.
public class CaseOnlyRenameTests
{
    private static void Build(TempRepo repo)
    {
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
    }

    private static void Apply(TempRepo repo, params string[] rels)
    {
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        using var snapshot = RepositoryIndexer.LoadSnapshot(repo.Root);
        try
        {
            RepositoryIndexer.ApplyChanges(text, symbols, snapshot, repo.Root, rels.Select(repo.FullPath));
            RepositoryIndexer.Persist(repo.Root, text, symbols, snapshot);
        }
        finally { text.Dispose(); symbols.Dispose(); }
    }

    private static string[] IndexedPaths(TempRepo repo)
    {
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        try { return text.AllPaths().OrderBy(p => p, System.StringComparer.Ordinal).ToArray(); }
        finally { text.Dispose(); symbols.Dispose(); }
    }

    private static bool CaseInsensitiveDisk(TempRepo repo) => File.Exists(repo.FullPath("src/WIDGET.CS"));

    [Fact]
    public void FileRenamedByCaseOnly_KeepsOnlyTheSpellingOnDisk()
    {
        using var repo = new TempRepo();
        repo.Write("src/Widget.cs", "class Widget {}");
        Build(repo);
        Assert.True(CaseInsensitiveDisk(repo)); // the case this guards: NTFS answers for the old spelling

        File.Move(repo.FullPath("src/Widget.cs"), repo.FullPath("src/widget.cs"));
        Apply(repo, "src/Widget.cs", "src/widget.cs");
        Assert.Equal(new[] { "src/widget.cs" }, IndexedPaths(repo));

        File.Move(repo.FullPath("src/widget.cs"), repo.FullPath("src/WIDGET.cs"));
        Apply(repo, "src/widget.cs", "src/WIDGET.cs");
        Assert.Equal(new[] { "src/WIDGET.cs" }, IndexedPaths(repo));
    }

    [Fact]
    public void FolderRenamedByCaseOnly_KeepsOnlyTheSpellingOnDisk()
    {
        using var repo = new TempRepo();
        repo.Write("Src/w.cs", "class W {}");
        repo.Write("Src/deep/d.cs", "class D {}");
        Build(repo);
        Assert.True(File.Exists(repo.FullPath("SRC/W.CS")));

        Directory.Move(repo.FullPath("Src"), repo.FullPath("tmp"));
        Directory.Move(repo.FullPath("tmp"), repo.FullPath("src"));
        Apply(repo, "Src", "src");
        Assert.Equal(new[] { "src/deep/d.cs", "src/w.cs" }, IndexedPaths(repo));
    }

    // The parent folder's case changed, and the events name the files under it: the old file name is spelled exactly,
    // its folder isn't.
    [Fact]
    public void ParentRenamedByCaseOnly_EventsForTheFiles_KeepOnlyTheSpellingOnDisk()
    {
        using var repo = new TempRepo();
        repo.Write("Src/w.cs", "class W {}");
        Build(repo);

        Directory.Move(repo.FullPath("Src"), repo.FullPath("tmp"));
        Directory.Move(repo.FullPath("tmp"), repo.FullPath("src"));
        Apply(repo, "Src/w.cs", "src/w.cs");
        Assert.Equal(new[] { "src/w.cs" }, IndexedPaths(repo));
    }

    // Only the folder is in the batch: its walk indexes the new spelling, and the old one, still answering, is dropped.
    [Fact]
    public void FileRenamedByCaseOnly_InsideAFolderInTheBatch_KeepsOnlyTheSpellingOnDisk()
    {
        using var repo = new TempRepo();
        repo.Write("src/Widget.cs", "class Widget {}");
        Build(repo);

        File.Move(repo.FullPath("src/Widget.cs"), repo.FullPath("src/widget.cs"));
        Apply(repo, "src");
        Assert.Equal(new[] { "src/widget.cs" }, IndexedPaths(repo));
    }

    // The event names the spelling on disk: nothing to drop, and the file is not re-added under another case.
    [Fact]
    public void EditUnderTheCurrentSpelling_IsUnchanged()
    {
        using var repo = new TempRepo();
        repo.Write("src/Widget.cs", "class Widget {}");
        Build(repo);
        repo.Write("src/Widget.cs", "class Widget { int X; }");
        Apply(repo, "src/Widget.cs");
        Assert.Equal(new[] { "src/Widget.cs" }, IndexedPaths(repo));
    }
}
