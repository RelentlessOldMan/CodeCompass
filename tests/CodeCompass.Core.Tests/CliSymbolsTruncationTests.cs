using System.Linq;
using System.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// `codecompass symbols` stopped at 200 matches and reported "-- 200 symbol(s)" as if that were all of them. The MCP
// search_symbols tool says MORE EXIST; the CLI must not present a cut-off list as complete either.
public class CliSymbolsTruncationTests
{
    [Fact]
    public void Symbols_OverTheLimit_SaysMoreExist()
    {
        using var repo = new TempRepo();
        var sb = new StringBuilder();
        for (int i = 0; i < 205; i++) sb.Append("class Widget").Append(i).Append(" { }\n");
        repo.Write("w.cs", sb.ToString());
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        var (exit, stdout, stderr) = TestCli.Run(exe, "symbols", repo.Root, "Widget");
        Assert.Equal(0, exit);
        Assert.Equal(200, stdout.Split('\n').Count(l => l.Contains("Widget")));
        Assert.Contains("MORE EXIST", stdout); // on stdout, so a redirected answer keeps it
    }

    [Fact]
    public void Symbols_UnderTheLimit_IsAPlainCount()
    {
        using var repo = new TempRepo();
        repo.Write("w.cs", "class WidgetA { }\nclass WidgetB { }\n");
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        var (_, stdout, stderr) = TestCli.Run(exe, "symbols", repo.Root, "Widget");
        Assert.Contains("-- 2 symbol(s)", stderr);
        Assert.DoesNotContain("MORE EXIST", stdout + stderr);
    }
}
