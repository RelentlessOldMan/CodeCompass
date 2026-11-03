using System.Linq;
using System.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// probe-repo.ps1 picks its benchmark names from `codecompass symbols`. A substring query comes back in name order and
// stops at 200, so every pool it built was names starting with "A". `symbols <path> --sample <n>` takes an even
// stride over the whole index instead.
public class CliSymbolsSampleTests
{
    [Fact]
    public void Sample_ComesFromAcrossTheIndex_NotOneEnd()
    {
        using var repo = new TempRepo();
        var a = new StringBuilder();
        var z = new StringBuilder();
        for (int i = 0; i < 300; i++)
        {
            a.Append("class Aardvark").Append(i).Append(" { }\n");
            z.Append("class Zebra").Append(i).Append(" { }\n");
        }
        repo.Write("a.cs", a.ToString());
        repo.Write("z.cs", z.ToString());
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        var (exit, stdout, stderr) = TestCli.Run(exe, "symbols", repo.Root, "--sample", "20");
        Assert.Equal(0, exit);
        var lines = stdout.Split('\n').Where(l => l.Contains(": Class ")).ToList();
        Assert.Equal(20, lines.Count);
        Assert.Contains(lines, l => l.Contains("Aardvark"));
        Assert.Contains(lines, l => l.Contains("Zebra"));
        Assert.Contains("-- 20 symbol(s)", stderr);
    }

    [Fact]
    public void Sample_NeedsAPositiveCount()
    {
        using var repo = new TempRepo();
        repo.Write("w.cs", "class Widget { }\n");
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        Assert.NotEqual(0, TestCli.Run(exe, "symbols", repo.Root, "--sample", "0").Exit);
        Assert.NotEqual(0, TestCli.Run(exe, "symbols", repo.Root, "--sample", "lots").Exit);
    }
}
