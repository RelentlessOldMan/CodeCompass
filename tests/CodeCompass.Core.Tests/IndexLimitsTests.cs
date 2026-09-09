using System;
using System.Linq;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Walking;
using Xunit;

namespace CodeCompass.Core.Tests;

// Escape hatches + visibility for the "dense generated headers" pathology: an operator can lower
// the size cap or exclude a directory without editing source, and over-cap skips are counted
// (not silent).
public class IndexLimitsTests
{
    [Fact]
    public void IgnoreRules_ReadsMaxFileMbFromEnv()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", "2");
            Assert.Equal(2L * 1024 * 1024, new IgnoreRules().MaxFileSizeBytes);
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", old); }
    }

    [Fact]
    public void IgnoreRules_ReadsIgnoreDirsFromEnv()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_IGNORE");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_IGNORE", "chipreg; generated");
            var r = new IgnoreRules();
            Assert.True(r.IsIgnoredDirectory("chipreg"));
            Assert.True(r.IsIgnoredDirectory("generated"));
            Assert.False(r.IsIgnoredDirectory("src"));
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_IGNORE", old); }
    }

    [Fact]
    public void Walker_ExcludesAndCountsOverCapFiles()
    {
        using var repo = new TempRepo();
        repo.WriteBytes("small.cs", new byte[100]);
        repo.WriteBytes("huge.cs", new byte[4096]);

        var walker = new FileWalker(new IgnoreRules(maxFileSizeBytes: 1024)); // 1 KB cap
        var rels = walker.Walk(repo.Root).Select(f => f.RelativePath).ToList();

        Assert.Contains("small.cs", rels);
        Assert.DoesNotContain("huge.cs", rels); // over the cap -> excluded
        Assert.Equal(1, walker.OverCapSkipped);
        Assert.EndsWith("huge.cs", walker.LargestOverCapPath);
        Assert.Equal(4096, walker.LargestOverCapBytes);
    }
}
