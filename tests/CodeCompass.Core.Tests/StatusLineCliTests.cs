using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CodeCompass.Core.Tests;

// The status line Claude Code renders on every turn. v1.0.239-241 shipped it garbled ("Opus Â· ... CodeCompass âœ“"):
// an edit re-saved the CLI source with its UTF-8 glyphs double-encoded. This pins the exact characters end to end.
public class StatusLineCliTests
{
    [Fact]
    public void Statusline_PrintsIntactGlyphs_ForAnIndexedRepo()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");

        Run(cli, null, "index", repo.Root);
        var stdin = JsonSerializer.Serialize(new { model = new { display_name = "Opus" }, workspace = new { current_dir = repo.Root } });
        var line = Run(cli, stdin, "statusline");

        Assert.StartsWith("Opus · ", line);          // the middle-dot separator
        Assert.Contains("CodeCompass ✓", line);      // the check mark for a ready index
        Assert.DoesNotContain("Â", line);            // 'Â' / 'â' only appear when UTF-8 was double-encoded
        Assert.DoesNotContain("â", line);
    }

    private static string Run(string cli, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "CLI did not exit");
        e.GetAwaiter().GetResult();
        return o.GetAwaiter().GetResult();
    }
}
