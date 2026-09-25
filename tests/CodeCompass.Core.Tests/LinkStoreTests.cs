using System.IO;
using System.Linq;
using CodeCompass.Core.Config;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

public class LinkStoreTests
{
    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      System.StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Add_Read_Remove_RoundTrips_AndIsIdempotent()
    {
        using var proj = new TempRepo();
        using var ext = new TempRepo();

        Assert.Empty(LinkStore.Read(proj.Root));
        Assert.True(LinkStore.Add(proj.Root, ext.Root));
        Assert.False(LinkStore.Add(proj.Root, ext.Root)); // already linked
        Assert.Contains(LinkStore.Read(proj.Root), r => Same(r, ext.Root));

        Assert.True(LinkStore.Remove(proj.Root, ext.Root));
        Assert.False(LinkStore.Remove(proj.Root, ext.Root)); // wasn't linked
        Assert.Empty(LinkStore.Read(proj.Root));
    }

    [Fact]
    public void ProjectsLinking_FindsOtherProjectsSharingTheRoot()
    {
        using var shared = new TempRepo();
        using var p1 = new TempRepo();
        using var p2 = new TempRepo();
        LinkStore.Add(p1.Root, shared.Root);
        LinkStore.Add(p2.Root, shared.Root);

        // From p1's perspective, p2 still references the shared root (so its index must NOT be deleted).
        var others = LinkStore.ProjectsLinking(shared.Root, excludingProjectRoot: p1.Root);
        Assert.Single(others);

        // With no exclusion, both projects are reported.
        Assert.Equal(2, LinkStore.ProjectsLinking(shared.Root).Count);
    }

    [Theory]
    [InlineData(@"C:\A\B", @"C:\A", true)]    // child inside parent
    [InlineData(@"C:\A", @"C:\A", true)]      // same directory
    [InlineData(@"C:\A\B\C", @"C:\A", true)]  // deeper child
    [InlineData(@"C:\A", @"C:\A\B", false)]   // parent is not "under" child
    [InlineData(@"C:\A\B", @"C:\C\D", false)] // unrelated
    [InlineData(@"C:\AB", @"C:\A", false)]    // shared prefix but NOT nested
    public void IsUnderOrEqual_DetectsNestingNotPrefixes(string child, string parent, bool expected)
        => Assert.Equal(expected, PathSafety.IsUnderOrEqual(child, parent));

    [Fact]
    public void TryRead_DistinguishesAbsentFromUnreadable()
    {
        using var proj = new TempRepo();

        // Absent links.json is a legitimate "no links" - succeeds with an empty list.
        Assert.True(LinkStore.TryRead(proj.Root, out var none));
        Assert.Empty(none);

        LinkStore.Add(proj.Root, Path.GetFullPath(@"C:\some\linked\root"));
        Assert.True(LinkStore.TryRead(proj.Root, out var one));
        Assert.Single(one);

        // A malformed file (not the empty state) must FAIL, so a live reconcile keeps its current set
        // instead of reading a garbage/partial write as "zero links."
        File.WriteAllText(Path.Combine(IndexStore.CacheDirPath(proj.Root), "links.json"), "{ this is not json");
        Assert.False(LinkStore.TryRead(proj.Root, out _));
    }

    [Fact]
    public void Signature_ChangesWhenLinksChange_ZeroWhenAbsent()
    {
        using var proj = new TempRepo();
        using var ext = new TempRepo();

        Assert.Equal(0, LinkStore.Signature(proj.Root)); // no file yet
        LinkStore.Add(proj.Root, ext.Root);
        var afterAdd = LinkStore.Signature(proj.Root);
        Assert.NotEqual(0, afterAdd);

        LinkStore.Remove(proj.Root, ext.Root);
        Assert.NotEqual(afterAdd, LinkStore.Signature(proj.Root)); // content shrank -> signature moved
    }

    [Fact]
    public void ExceedsAutoLimit_SmallTree_IsUnderTheDefaultCap()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");
        CodeCompassConfig.Load(repo.Root); // default maxAutoMb (100 MB)
        Assert.False(RepositoryIndexer.ExceedsAutoLimit(repo.Root, out var total));
        Assert.True(total >= 0);
    }

    [Fact]
    public void ExceedsAutoLimit_OverCap_Defers_ForFreshLinkedTree()
    {
        // The decision `link add` makes on a fresh, UNINDEXED external tree: with the linked root's own
        // .codecompass.json setting a maxAutoMb of 0 (a valid "force CLI build" value), any non-empty tree
        // is over the cap, so link add must DEFER (print the threshold + build nothing) rather than index
        // inline. This guards the branch behind the size-gate message that was previously misreported.
        using var linked = new TempRepo();
        linked.Write("code.c", "int f(void){return 0;}");
        linked.Write(".codecompass.json", """{ "maxAutoMb": 0 }""");

        var cfg = CodeCompassConfig.ReadFrom(linked.Root)!;
        Assert.True(RepositoryIndexer.ExceedsAutoLimit(linked.Root, cfg, out var total));
        Assert.True(total > 0); // it crossed the (zero) cap on a real file

        // And the honest reported number is the THRESHOLD, not this partial floor.
        Assert.Equal(0, CodeCompassConfig.MaxAutoBytes(cfg));
    }
}
