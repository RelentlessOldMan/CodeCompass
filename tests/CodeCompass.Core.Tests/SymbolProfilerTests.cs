using System.Linq;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

public class SymbolProfilerTests
{
    [Fact]
    public void ProfilesSymbolBearingFilesPerLanguage()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() {} }\n");
        repo.Write("b.cs", "class B { int F; void N(int x) {} }\n");
        repo.Write("notcode.txt", "class C {}\n"); // no grammar -> ignored by the profiler

        var r = SymbolProfiler.Profile(repo.Root);

        var cs = Assert.Single(r.Languages, p => p.Extension == ".cs");
        Assert.Equal(2, cs.FilesParsed);
        Assert.Equal(2, cs.FilesWithSymbols);
        Assert.True(cs.SymbolBearingMax > 0);
        Assert.DoesNotContain(r.Languages, p => p.Extension == ".txt");
        // Nothing pathological here, so no knee was tripped.
        Assert.All(r.Languages, p => Assert.Null(p.Knee));
    }

    [Fact]
    public void AscendingScanNeverParsesAboveHardCeiling()
    {
        using var repo = new TempRepo();
        repo.Write("small.cs", "class A {}\n");
        // A file over the hard ceiling must be counted as not-profiled, never read/parsed.
        repo.Write("big.cs", new string('x', 4096));

        var r = SymbolProfiler.Profile(repo.Root, timeoutMs: 5000, hardCeilingBytes: 1024);
        var cs = Assert.Single(r.Languages, p => p.Extension == ".cs");
        Assert.Equal(1, cs.FilesParsed);               // only small.cs
        Assert.True(cs.LargerNotProfiled >= 1);        // big.cs skipped by the ceiling
    }
}

public class ParseSweepTests
{
    [Fact]
    public void ProducesLinearGrowthPointsForOrdinaryCode()
    {
        // Cheap sweep: .cs only, capped at 256 KB. "code" should parse fast and yield symbols.
        var series = ParseSweep.Run(ceilingMs: 10_000, extensions: new[] { ".cs" }, maxBytes: 256 * 1024);

        var code = Assert.Single(series, s => s.Shape == "code");
        Assert.NotEmpty(code.Points);
        Assert.All(code.Points, p => Assert.False(p.TimedOut));
        Assert.Contains(code.Points, p => p.Symbols > 0);
    }
}
