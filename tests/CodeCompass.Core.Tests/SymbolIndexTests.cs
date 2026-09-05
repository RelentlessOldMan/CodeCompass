using System.IO;
using System.Linq;
using CodeCompass.Core.Symbols;
using Xunit;

namespace CodeCompass.Core.Tests;

public class SymbolIndexTests
{
    private static SymbolIndex Sample()
    {
        return SymbolIndex.Build(new[]
        {
            new Symbol("Foo", SymbolKind.Class, "src/a.cs", 3, 5),
            new Symbol("Foo", SymbolKind.Method, "src/b.cs", 10, 9),   // same name, different place
            new Symbol("Bar", SymbolKind.Method, "src/a.cs", 7, 5),
            new Symbol("FooBar", SymbolKind.Function, "src/c.py", 1, 1),
        });
    }

    [Fact]
    public void FindByName_ReturnsAllExactMatches()
    {
        var idx = Sample();
        var foos = idx.FindByName("Foo");
        Assert.Equal(2, foos.Count);
        Assert.Contains(foos, s => s.Kind == SymbolKind.Class && s.RelativePath == "src/a.cs");
        Assert.Contains(foos, s => s.Kind == SymbolKind.Method && s.RelativePath == "src/b.cs");
    }

    [Fact]
    public void FindByName_IsCaseSensitive_AndExact()
    {
        var idx = Sample();
        Assert.Empty(idx.FindByName("foo"));    // wrong case
        Assert.Empty(idx.FindByName("Fo"));     // not exact
    }

    [Fact]
    public void Find_SubstringIsCaseInsensitive()
    {
        var idx = Sample();
        var hits = idx.Find("foo").Select(s => s.Name).ToHashSet();
        Assert.Contains("Foo", hits);
        Assert.Contains("FooBar", hits);
        Assert.DoesNotContain("Bar", hits);
    }

    [Fact]
    public void SurvivesSaveAndLoad()
    {
        var idx = Sample();
        using var ms = new MemoryStream();
        idx.Save(ms);
        ms.Position = 0;
        var loaded = SymbolIndex.Load(ms);

        Assert.Equal(idx.Count, loaded.Count);
        Assert.Equal(2, loaded.FindByName("Foo").Count);
        var bar = Assert.Single(loaded.FindByName("Bar"));
        Assert.Equal("src/a.cs", bar.RelativePath);
        Assert.Equal(7, bar.Line);
        Assert.Equal(SymbolKind.Method, bar.Kind);
    }
}
