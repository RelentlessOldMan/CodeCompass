using System;
using System.Threading.Tasks;
using Xunit;

namespace CodeCompass.Core.Tests;

// CLI messages that field reports asked for and nothing else pins: the core/MCP guards are tested elsewhere,
// these check what a terminal user actually sees.
[Collection("compaction-env")] // one test sets the process-wide CODECOMPASS_FORCE_NETWORK (inherited by the child)
public class CliMessagesTests
{
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("  \t ")]
    public void Search_WhitespaceOnlyQuery_ExitsTwoWithGuidance_NoResults(string query)
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a b c d e f\n g h i j\n");
        var cli = TestCli.Find();
        Assert.Equal(0, TestCli.Run(cli, "index", repo.Root).Exit);

        var (exit, stdout, stderr) = TestCli.Run(cli, "search", repo.Root, query);

        Assert.Equal(2, exit);
        Assert.Contains("Provide a non-empty search string.", stderr);
        Assert.DoesNotContain("a.txt", stdout); // no line dump
    }

    [Fact]
    public void Survey_SaysIndexedIsAPreIndexEstimate()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "class A { }");
        var (exit, stdout, _) = TestCli.Run(TestCli.Find(), "survey", repo.Root);

        Assert.Equal(0, exit);
        Assert.Contains("'Indexed' is a pre-index ESTIMATE", stdout);
        Assert.Contains("by design, not a discrepancy", stdout);
    }

    [Fact]
    public async Task Watch_OnNetworkPath_WarnsThatEventsAreUnreliable()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");
        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        System.Diagnostics.Process p;
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            p = TestCli.Start(TestCli.Find(), "watch", repo.Root);
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prev); }

        using (p)
        {
            try
            {
                // The note is the first thing watch writes to stderr; read just that line, then stop it.
                var first = await p.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Contains("watching a network path", first);
            }
            finally { try { p.Kill(entireProcessTree: true); p.WaitForExit(10_000); } catch { } }
        }
    }
}
