using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// The MCP tool handler is the surface real users hit (via Claude/Codex), and it had ~no direct coverage -
// which is how the false-zero stayed latent on the MCP path. This drives the actual built MCP server over
// stdio (initialize -> tools/call find_references) and asserts the product-surface behavior end-to-end.
// Soft-skips if the server exe isn't built (bare `dotnet test`); the release gate builds it and runs this.
public class McpFindReferencesTests
{
    [Fact]
    public void Mcp_FindReferences_MissingHeader_CallsStillFound()
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

        // Over the real stdio server: every call site comes back even though the header doesn't exist (C/C++ is a
        // name search), and the answer says what C/C++ matches are.
        for (int i = 0; i < 4; i++) Assert.Contains($"mod{i}.c:2:", text);
        Assert.Contains("matched by NAME", text);
    }

    // The new manage_links MCP tool, end-to-end: add a linked root over stdio, then a federated search_code
    // must find a token that exists ONLY in the linked root (proving the add federated it), and list must
    // show it. This is the product-surface test for managing links from the agent (not just the CLI).
    [Fact]
    public void Mcp_ManageLinks_AddFederatesLinkedRoot()
    {
        var mcp = FindExe("CodeCompass.Mcp");
        if (mcp is null) return;

        using var project = new TempRepo();
        project.Write("app.c", "int app(void){ return 0; }\n");
        using var lib = new TempRepo();
        lib.Write("lib.c", "int zzq_linked_marker_7a3f(void){ return 1; }\n"); // token exists ONLY in the linked root

        var (t, s, _) = RepositoryIndexer.Build(project.Root); t.Dispose(); s.Dispose();

        try
        {
            var (addResp, searchResp, listResp) = McpSession(mcp, project.Root, session =>
            {
                var add = session("manage_links", $"{{\"action\":\"add\",\"path\":{JsonEncode(lib.Root)}}}");
                var search = session("search_code", "{\"query\":\"zzq_linked_marker_7a3f\",\"maxResults\":10}");
                var list = session("manage_links", "{\"action\":\"list\"}");
                return (add, search, list);
            });

            Assert.Contains("linked:", addResp);
            Assert.Contains("zzq_linked_marker_7a3f", searchResp); // federated: found in the linked root
            Assert.Contains("lib.c", searchResp);
            Assert.Contains(Path.GetFileName(lib.Root), listResp); // list shows the linked root's dir (temp dirs are guids, not "lib")
        }
        finally
        {
            // Clean up the linked root's index so the test doesn't leave a shared cache around.
            LinkManager.Remove(project.Root, lib.Root, _ => true);
        }
    }

    private static string JsonEncode(string s) => JsonSerializer.Serialize(s);

    private static string McpFindReferences(string exe, string root, string symbol)
        => McpSession(exe, root, call => call("find_references", "{\"name\":\"" + symbol + "\",\"maxResults\":100}"));

    // Minimal stdio JSON-RPC client: spawns the server, does the initialize handshake, then hands the body a
    // `call(tool, argsJson) -> resultText` function (newline-delimited JSON, the MCP stdio framing). One
    // helper so the test client itself isn't duplicated across tests.
    private static T McpSession<T>(string exe, string root, Func<Func<string, string, string>, T> body)
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
        int nextId = 1;

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
            Assert.NotNull(ReadUntilId(1, 30000));
            nextId = 1;
            Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

            Func<string, string, string> call = (tool, argsJson) =>
            {
                int id = ++nextId;
                Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + argsJson + "}}");
                var resp = ReadUntilId(id, 120000);
                Assert.False(string.IsNullOrEmpty(resp), $"no tools/call response for {tool}");
                using var doc = JsonDocument.Parse(resp!);
                var content = doc.RootElement.GetProperty("result").GetProperty("content");
                var sb = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("text", out var tx)) sb.AppendLine(tx.GetString());
                return sb.ToString();
            };
            return body(call);
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
