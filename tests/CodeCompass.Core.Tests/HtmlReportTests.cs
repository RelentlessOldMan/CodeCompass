using System.Collections.Generic;
using CodeCompass.Bench;
using Xunit;

namespace CodeCompass.Core.Tests;

public class HtmlReportTests
{
    private static BenchResult FakeBench(string name) => new(
        Name: name, Files: 5000, Bytes: 63_400_000, Trigrams: 107_000, Symbols: 98_000,
        BuildSeconds: 4.2, BuildMBps: 15.1, Cores: 8, BuildMBpsPerCore: 1.9,
        IndexBytes: 23_700_000, IndexRatio: 0.37,
        QueryP50Ms: 1.8, QueryP95Ms: 22.7, QueryP99Ms: 36.5,
        PeakWorkingSetMb: 162, IncrementalSeconds: 0.97, IncrementalFiles: 10);

    [Fact]
    public void Generate_ProducesSelfContainedHtml()
    {
        var rows = new List<ReportRow>
        {
            new("efcore", "csharp", FakeBench("efcore"), "PASS"),
            new("godot", "cpp", FakeBench("godot"), "FAIL (2)"),
        };
        var meta = new ReportMeta("2026-09-05 12:00", "PC", "Windows", 8, ".NET 8.0");

        var html = HtmlReport.Generate(rows, meta, new[] { "llvm" });

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("<style>", html);          // inline CSS => self-contained
        Assert.DoesNotContain("http://", html);    // no external assets
        Assert.DoesNotContain("https://", html);
        Assert.Contains("efcore", html);
        Assert.Contains("15.1", html);             // MB/s value rendered
        Assert.Contains("badge pass", html);
        Assert.Contains("badge fail", html);
        Assert.Contains("llvm", html);             // skipped note
    }
}
