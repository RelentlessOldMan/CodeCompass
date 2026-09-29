using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// LinkManager is the shared add/remove/list logic behind BOTH the CLI `link` commands and the MCP
// manage_links tool. These pin its decisions (nesting guards, index-or-reuse, shared-index safety on
// remove) so the two hosts can't drift.
public class LinkManagerTests
{
    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Add_List_Remove_RoundTrip()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        lib.Write("lib.c", "int lib_fn(int x){ return x; }\n");

        var add = LinkManager.Add(project.Root, lib.Root);
        Assert.True(add.Status is LinkManager.AddStatus.Indexed or LinkManager.AddStatus.ReusedIndex, add.Message);

        var list = LinkManager.List(project.Root);
        Assert.Single(list);
        Assert.True(SamePath(list[0].Path, lib.Root));
        Assert.True(list[0].Indexed, $"expected the linked lib to be indexed: {list[0].Status}");

        // Adding the same root again is a no-op.
        Assert.Equal(LinkManager.AddStatus.AlreadyLinked, LinkManager.Add(project.Root, lib.Root).Status);

        // Remove + purge (no other project links it) deletes the index and empties the list.
        var rem = LinkManager.Remove(project.Root, lib.Root, _ => true);
        Assert.Equal(LinkManager.RemoveStatus.UnlinkedPurged, rem.Status);
        Assert.Empty(LinkManager.List(project.Root));

        // Removing again reports not-linked.
        Assert.Equal(LinkManager.RemoveStatus.NotLinked, LinkManager.Remove(project.Root, lib.Root, _ => true).Status);
    }

    [Fact]
    public void Add_NestedOrSelf_Rejected()
    {
        using var project = new TempRepo();
        var sub = Path.Combine(project.Root, "sub");
        Directory.CreateDirectory(sub);
        Assert.Equal(LinkManager.AddStatus.Rejected, LinkManager.Add(project.Root, sub).Status);       // inside the project
        Assert.Equal(LinkManager.AddStatus.Rejected, LinkManager.Add(project.Root, project.Root).Status); // the project itself
    }

    [Fact]
    public void Remove_SharedIndex_KeptWhileAnotherProjectLinksIt()
    {
        using var projA = new TempRepo();
        using var projB = new TempRepo();
        using var lib = new TempRepo();
        lib.Write("lib.c", "int lib_fn(int x){ return x; }\n");

        LinkManager.Add(projA.Root, lib.Root);
        LinkManager.Add(projB.Root, lib.Root); // shared root: indexed once, referenced by both

        var rem = LinkManager.Remove(projA.Root, lib.Root, _ => true); // even with purge=yes...
        Assert.Equal(LinkManager.RemoveStatus.UnlinkedShared, rem.Status); // ...index is kept - B still links it
        Assert.NotEmpty(rem.OtherProjects);

        LinkManager.Remove(projB.Root, lib.Root, _ => true); // cleanup: last reference, purge the shared index
    }
}
