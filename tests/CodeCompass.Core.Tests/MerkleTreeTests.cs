using System.Collections.Generic;
using CodeCompass.Core.Changes;
using Xunit;

namespace CodeCompass.Core.Tests;

public class MerkleTreeTests
{
    private static Dictionary<string, FileState> Snap(params (string path, string hash)[] files)
    {
        var d = new Dictionary<string, FileState>();
        foreach (var (path, hash) in files)
            d[path] = new FileState(Size: hash.Length, MTimeTicks: 0, ContentHash: hash);
        return d;
    }

    [Fact]
    public void IdenticalTrees_HaveEqualRootHash_AndEmptyDiff()
    {
        var a = MerkleTree.Build(Snap(("src/a.cs", "H1"), ("src/b.cs", "H2"), ("readme.md", "H3")));
        var b = MerkleTree.Build(Snap(("src/a.cs", "H1"), ("src/b.cs", "H2"), ("readme.md", "H3")));

        Assert.Equal(a.RootHash, b.RootHash);
        Assert.True(b.Diff(a).IsEmpty);
    }

    [Fact]
    public void ModifiedFile_IsDetected_Alone()
    {
        var oldT = MerkleTree.Build(Snap(("src/a.cs", "H1"), ("src/b.cs", "H2")));
        var newT = MerkleTree.Build(Snap(("src/a.cs", "H1"), ("src/b.cs", "CHANGED")));

        var diff = newT.Diff(oldT);
        Assert.Equal(new[] { "src/b.cs" }, diff.Modified);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void AddedAndRemoved_AreClassified()
    {
        var oldT = MerkleTree.Build(Snap(("keep.cs", "H1"), ("gone.cs", "H2")));
        var newT = MerkleTree.Build(Snap(("keep.cs", "H1"), ("new.cs", "H3")));

        var diff = newT.Diff(oldT);
        Assert.Equal(new[] { "new.cs" }, diff.Added);
        Assert.Equal(new[] { "gone.cs" }, diff.Removed);
        Assert.Empty(diff.Modified);
    }

    [Fact]
    public void TouchedButIdenticalContent_IsNotAChange()
    {
        // Same content hashes, different mtimes/sizes: the tree only trusts content,
        // so nothing should be reported as changed.
        var oldT = MerkleTree.Build(new Dictionary<string, FileState>
        {
            ["a.cs"] = new FileState(10, MTimeTicks: 100, "SAME"),
        });
        var newT = MerkleTree.Build(new Dictionary<string, FileState>
        {
            ["a.cs"] = new FileState(999, MTimeTicks: 500, "SAME"),
        });

        Assert.Equal(oldT.RootHash, newT.RootHash);
        Assert.True(newT.Diff(oldT).IsEmpty);
    }
}
