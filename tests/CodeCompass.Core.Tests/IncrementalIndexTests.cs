using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

public class IncrementalIndexTests
{
    // Rewrite a file and force a distinct modified-time so change detection can't be fooled.
    private static void WriteBumped(TempRepo repo, string rel, string content, int secondsAhead)
    {
        repo.Write(rel, content);
        var full = Path.Combine(repo.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(secondsAhead));
    }

    private static HashSet<string> SearchSet(SegmentedIndex idx, string q) =>
        idx.Search(q, 1_000_000).Select(m => $"{m.Path}:{m.Line}:{m.Column}").ToHashSet();

    private static HashSet<string> NameSet(SegmentedSymbolIndex idx, string n) =>
        idx.FindByName(n).Select(s => $"{s.RelativePath}:{s.Line}:{s.Column}:{s.Kind}").ToHashSet();

    [Fact]
    public void IncrementalUpdate_MatchesFullRebuild()
    {
        using var repo = new TempRepo();
        WriteBumped(repo, "a.cs", "namespace N { class Alpha { void One() { } } }", 1);
        WriteBumped(repo, "b.cs", "namespace N { class Beta { void Two() { } } }", 1);
        WriteBumped(repo, "c.txt", "hello world", 1);
        WriteBumped(repo, "d.cs", "namespace N { class Delta { } }", 1);

        RepositoryIndexer.Build(repo.Root);

        // Modify a.cs, add e.cs, delete b.cs, touch c.txt (same content, new mtime).
        WriteBumped(repo, "a.cs", "namespace N { class Alpha { void OneRenamed() { } } }", 60);
        WriteBumped(repo, "e.cs", "namespace N { class Echo { } }", 60);
        File.Delete(Path.Combine(repo.Root, "b.cs"));
        WriteBumped(repo, "c.txt", "hello world", 60); // identical content, bumped mtime

        var (incText, incSymbols, stats) = RepositoryIndexer.Update(repo.Root);

        Assert.False(stats.FullRebuild);
        Assert.Equal(1, stats.Modified); // a.cs
        Assert.Equal(1, stats.Added);    // e.cs
        Assert.Equal(1, stats.Removed);  // b.cs
        // c.txt was touched but identical -> not counted as a change.

        // A from-scratch rebuild of the current tree is the ground truth.
        var (fullText, fullSymbols, _) = RepositoryIndexer.Build(repo.Root);

        Assert.Equal(fullText.DocumentCount, incText.DocumentCount);
        // (symbol Count is a raw, pre-compaction count once tombstones exist; correctness is
        // verified by the name-set comparisons below, not the raw count.)

        foreach (var q in new[] { "Alpha", "OneRenamed", "One", "Beta", "Two", "Echo", "Delta", "hello", "class" })
            Assert.True(SearchSet(incText, q).SetEquals(SearchSet(fullText, q)), $"search mismatch for '{q}'");

        foreach (var n in new[] { "Alpha", "OneRenamed", "Beta", "Two", "Echo", "Delta" })
            Assert.True(NameSet(incSymbols, n).SetEquals(NameSet(fullSymbols, n)), $"symbol mismatch for '{n}'");

        // Sanity: deleted/renamed symbols are truly gone.
        Assert.Empty(incSymbols.FindByName("Beta"));
        Assert.Empty(incSymbols.FindByName("One"));
        Assert.NotEmpty(incSymbols.FindByName("Echo"));
    }

    [Fact]
    public void TargetedUpdate_MatchesFullRebuild()
    {
        using var repo = new TempRepo();
        WriteBumped(repo, "a.cs", "namespace N { class Alpha { void One() { } } }", 1);
        WriteBumped(repo, "keep.cs", "namespace N { class Keep { } }", 1);
        WriteBumped(repo, "dir/x.cs", "namespace N { class Ex { } }", 1);
        WriteBumped(repo, "dir/y.cs", "namespace N { class Why { } }", 1);
        WriteBumped(repo, "gone.cs", "namespace N { class Gone { } }", 1);
        RepositoryIndexer.Build(repo.Root);

        // Modify a file, add a file, delete a file, and delete a whole directory.
        WriteBumped(repo, "a.cs", "namespace N { class Alpha { void OneRenamed() { } } }", 60);
        WriteBumped(repo, "added.cs", "namespace N { class Added { } }", 60);
        File.Delete(Path.Combine(repo.Root, "gone.cs"));
        Directory.Delete(Path.Combine(repo.Root, "dir"), recursive: true);

        string Full(string rel) => Path.Combine(repo.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        var changed = new[] { Full("a.cs"), Full("added.cs"), Full("gone.cs"), Full("dir") };

        var (incText, incSymbols, _) = RepositoryIndexer.UpdatePaths(repo.Root, changed);
        var (fullText, fullSymbols, _) = RepositoryIndexer.Build(repo.Root);

        Assert.Equal(fullText.DocumentCount, incText.DocumentCount);

        foreach (var q in new[] { "Alpha", "OneRenamed", "One", "Keep", "Ex", "Why", "Gone", "Added", "class" })
            Assert.True(SearchSet(incText, q).SetEquals(SearchSet(fullText, q)), $"search mismatch for '{q}'");

        foreach (var n in new[] { "Alpha", "OneRenamed", "Keep", "Ex", "Why", "Gone", "Added" })
            Assert.True(NameSet(incSymbols, n).SetEquals(NameSet(fullSymbols, n)), $"symbol mismatch for '{n}'");

        // Deleted file, deleted directory's files, and the renamed method are all gone.
        Assert.Empty(incSymbols.FindByName("Gone"));
        Assert.Empty(incSymbols.FindByName("Ex"));
        Assert.Empty(incSymbols.FindByName("Why"));
        Assert.Empty(incSymbols.FindByName("One"));
        Assert.NotEmpty(incSymbols.FindByName("Added"));
    }

    [Fact]
    public void Update_WithNoChanges_IsANoOp()
    {
        using var repo = new TempRepo();
        WriteBumped(repo, "a.cs", "namespace N { class A { } }", 1);
        RepositoryIndexer.Build(repo.Root);

        var (_, _, stats) = RepositoryIndexer.Update(repo.Root);
        Assert.Equal(0, stats.Added);
        Assert.Equal(0, stats.Modified);
        Assert.Equal(0, stats.Removed);
        Assert.False(stats.FullRebuild);
    }

    [Fact]
    public void Update_WithoutPriorIndex_FallsBackToFullBuild()
    {
        using var repo = new TempRepo();
        WriteBumped(repo, "a.cs", "namespace N { class A { } }", 1);

        var (_, symbols, stats) = RepositoryIndexer.Update(repo.Root);
        Assert.True(stats.FullRebuild);
        Assert.NotEmpty(symbols.FindByName("A"));
    }
}
