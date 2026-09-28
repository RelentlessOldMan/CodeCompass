using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// The MCP tool handler is the surface real users hit (via Claude/Codex), and it had ~no direct coverage -
// which is how the false-zero stayed latent on the MCP path. This drives the actual built MCP server over
// stdio (initialize -> tools/call find_references) and asserts the product-surface behavior end-to-end.
// Soft-skips if the server exe isn't built (bare `dotnet test`); the release gate builds it and runs this.
public class McpFindReferencesTests
{
    [Fact]
    public void Mcp_FindReferences_UnresolvedInclude_BackfillsLexical()
    {
        var mcp = FindExe("CodeCompass.Mcp");
        if (mcp is null) return;

        using var repo = new TempRepo();
        for (int i = 0; i < 4; i++)
            repo.Write($"mod{i}.c", $"#include \"hwdefs_missing.h\"\nint use_{i}(void){{ return widget_reset({i}); }}\n");

        // Build the index in-process so the server has candidates to resolve.
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();

        var text = McpFindReferences(mcp, repo.Root, "widget_reset");

        // The false-zero fix on the MCP surface: NOT a bare zero - the real call sites come back (lexical),
        // and the incomplete-coverage caveat is disclosed.
        Assert.Contains("widget_reset", text);
        Assert.True(text.Contains("mod0.c") || text.Contains("mod1.c"), $"expected call sites in the result; got:\n{text}");
        Assert.Contains("unresolved #include", text);
    }

    // Minimal stdio JSON-RPC client: initialize -> initialized -> tools/call find_references; returns the
    // tool result's text. Newline-delimited JSON (the MCP stdio framing).
    private static string McpFindReferences(string exe, string root, string symbol)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(root);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(); // drain stderr (server logs) so it can't fill the pipe

        void Send(string json) { p.StandardInput.WriteLine(json); p.StandardInput.Flush(); }
        string? ReadUntilId(int id, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var lineTask = p.StandardOutput.ReadLineAsync();
                if (!lineTask.Wait(deadline - DateTime.UtcNow)) return null;
                var line = lineTask.Result;
                if (line is null) return null;
                if (line.Contains($"\"id\":{id}")) return line;
            }
            return null;
        }

        try
        {
            Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}");
            Assert.NotNull(ReadUntilId(1, 30000)); // server initialized
            Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var argsJson = "{\"name\":\"find_references\",\"arguments\":{\"name\":\"" + symbol + "\",\"maxResults\":100}}";
            Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":" + argsJson + "}");
            var resp = ReadUntilId(2, 120000);
            Assert.False(string.IsNullOrEmpty(resp), "no tools/call response from the MCP server");

            using var doc = JsonDocument.Parse(resp!);
            var content = doc.RootElement.GetProperty("result").GetProperty("content");
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("text", out var tx)) sb.AppendLine(tx.GetString());
            return sb.ToString();
        }
        finally
        {
            try { p.StandardInput.Close(); } catch { }
            if (!p.WaitForExit(5000)) { try { p.Kill(entireProcessTree: true); } catch { } }
            _ = err; // observed
        }
    }

    private static string? FindExe(string baseName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", baseName, "bin");
            if (Directory.Exists(bin))
                return Directory.EnumerateFiles(bin, baseName + ".exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }
}
