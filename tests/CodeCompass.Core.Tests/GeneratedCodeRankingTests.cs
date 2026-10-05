using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using CodeCompass.Core.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// Field report v4: `symbols Manager` on a ~20-project solution returned 20 identical-looking `ResourceManager` hits -
// one per project's generated Resources.Designer.cs - crowding the hand-written matches. They're real symbols, so they
// stay, but tool-generated files rank AFTER hand-written ones, and the demotion happens before the result cap.
public class GeneratedCodeRankingTests
{
    [Theory]
    [InlineData("App/Properties/Resources.Designer.cs", true)]
    [InlineData("App/Form1.designer.cs", true)]
    [InlineData("App/My Project/Settings.Designer.vb", true)]
    [InlineData("obj/Debug/App.g.cs", true)]
    [InlineData("obj/Debug/MainWindow.g.i.cs", true)]
    [InlineData("Proto/Messages.generated.cs", true)]
    [InlineData("App/ResourceManager.cs", false)]
    [InlineData("App/Designer.cs", false)]                 // a hand-written file merely NAMED Designer
    [InlineData("App/Config.cs", false)]
    public void IsGeneratedPath_RecognizesToolOutput(string path, bool generated) =>
        Assert.Equal(generated, GeneratedCode.IsGeneratedPath(path));

    private static Symbol[] Mixed() =>
        Enumerable.Range(1, 5).Select(i => new Symbol("ResourceManager", SymbolKind.Property, $"P{i}/Properties/Resources.Designer.cs", 9, 66))
        .Concat(new[]
        {
            new Symbol("TestKernelManager", SymbolKind.Class, "Core/TestKernelManager.cs", 25, 18),
            new Symbol("PluginManager", SymbolKind.Class, "Core/PluginManager.cs", 10, 14),
        }).ToArray();

    [Fact]
    public void SegmentedFind_RanksHandWrittenFirst_AndCapDoesNotStarveThem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var idx = SegmentedSymbolIndex.Create(dir, 1L << 20);
            foreach (var s in Mixed()) idx.Add(s);   // generated ones indexed FIRST - the old first-N order showed only them
            idx.Flush();

            var all = idx.Find("Manager");
            Assert.Equal(7, all.Count);
            Assert.Equal(new[] { "PluginManager", "TestKernelManager" }, all.Take(2).Select(s => s.Name).OrderBy(n => n)); // within-tier order is the index's own
            Assert.All(all.Skip(2), s => Assert.Equal("ResourceManager", s.Name));

            var capped = idx.Find("Manager", max: 3);
            Assert.Equal(3, capped.Count);
            Assert.Contains(capped, s => s.Name == "TestKernelManager");
            Assert.Contains(capped, s => s.Name == "PluginManager");

            Assert.Equal(5, idx.Find("ResourceMan").Count); // generated-only matches are still returned
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void LegacyFind_RanksHandWrittenFirst()
    {
        var idx = new SymbolIndex();
        foreach (var s in Mixed()) idx.Add(s);
        var capped = idx.Find("Manager", max: 2);
        Assert.Equal(new[] { "TestKernelManager", "PluginManager" }, capped.Select(s => s.Name));
    }
}
