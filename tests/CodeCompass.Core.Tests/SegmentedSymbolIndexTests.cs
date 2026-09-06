using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

public class SegmentedSymbolIndexTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-sym-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static readonly Symbol[] Sample =
    {
        new("Foo", SymbolKind.Class, "a.cs", 1, 1),
        new("Foo", SymbolKind.Method, "b.cs", 5, 3),
        new("Bar", SymbolKind.Method, "a.cs", 7, 1),
        new("FooBar", SymbolKind.Function, "c.py", 2, 1),
    };

    private static SegmentedSymbolIndex Build(string dir, long budget)
    {
        var idx = SegmentedSymbolIndex.Create(dir, budget);
        foreach (var s in Sample) idx.Add(s);
        idx.Flush();
        return idx;
    }

    [Fact]
    public void FindByName_And_Substring_AcrossSegments()
    {
        var dir = NewTempDir();
        try
        {
            using var idx = Build(dir, budget: 1); // force one segment per symbol
            Assert.True(idx.SegmentCount >= 2);

            var foos = idx.FindByName("Foo");
            Assert.Equal(2, foos.Count);
            Assert.Contains(foos, s => s.Kind == SymbolKind.Class && s.RelativePath == "a.cs");
            Assert.Contains(foos, s => s.Kind == SymbolKind.Method && s.RelativePath == "b.cs");

            Assert.Empty(idx.FindByName("foo"));  // exact + case-sensitive

            var sub = idx.Find("foo").Select(s => s.Name).ToHashSet();
            Assert.Contains("Foo", sub);
            Assert.Contains("FooBar", sub);
            Assert.DoesNotContain("Bar", sub);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RemovePath_TombstonesByPath()
    {
        var dir = NewTempDir();
        try
        {
            using var idx = Build(dir, budget: 1);
            Assert.Equal(2, idx.FindByName("Foo").Count);

            idx.RemovePath("b.cs"); // removes the Foo in b.cs, keeps the one in a.cs
            idx.Flush();

            var foos = idx.FindByName("Foo");
            Assert.Single(foos);
            Assert.Equal("a.cs", foos[0].RelativePath);
            Assert.NotEmpty(idx.FindByName("Bar")); // Bar is in a.cs, untouched
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SurvivesReopen()
    {
        var dir = NewTempDir();
        try
        {
            using (var idx = Build(dir, budget: 1)) { idx.RemovePath("a.cs"); idx.Flush(); }

            using var reopened = SegmentedSymbolIndex.Open(dir);
            Assert.Empty(reopened.FindByName("Bar"));         // a.cs tombstone persisted
            Assert.Single(reopened.FindByName("Foo"));        // only b.cs's Foo remains
            Assert.Equal("b.cs", reopened.FindByName("Foo")[0].RelativePath);
        }
        finally { Directory.Delete(dir, true); }
    }
}
