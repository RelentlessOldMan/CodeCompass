using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeCompass.Core.SelfTest;

/// <summary>One tool call's outcome: the answer text, how long it took, and the server's peak working set during it.</summary>
public sealed record McpCallResult(string Text, long Ms, double PeakMb, bool IsError, bool TimedOut);

/// <summary>
/// The MCP server as Claude Code and Codex run it: the real exe over stdio (newline-delimited JSON-RPC), started for one
/// root, with its memory sampled the whole session. Calls are sequential, as an agent makes them.
/// </summary>
public sealed class McpStdioSession : IDisposable
{
    private readonly Process _p;
    private readonly ProcessSampler _sampler;
    private readonly Queue<string> _stderrTail = new();
    private int _nextId;
    private Task<string?>? _pendingRead;

    private McpStdioSession(Process p, long ceilingBytes)
    {
        _p = p;
        _sampler = new ProcessSampler(p, ceilingBytes);
    }

    public ProcessSampler Memory => _sampler;
    public bool Exited { get { try { return _p.HasExited; } catch { return true; } } }
    public int ProcessId => _p.Id;

    /// <summary>The server's last lines of log on stderr (kept for a report when it dies).</summary>
    public string StderrTail { get { lock (_stderrTail) return string.Join('\n', _stderrTail); } }

    /// <summary>Start the server for <paramref name="root"/> and do the initialize handshake.</summary>
    public static McpStdioSession Start(string mcpExe, string root, long ceilingBytes, TimeSpan handshakeTimeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = mcpExe, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(root);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + mcpExe);
        var s = new McpStdioSession(p, ceilingBytes);
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (s._stderrTail) { s._stderrTail.Enqueue(e.Data); while (s._stderrTail.Count > 40) s._stderrTail.Dequeue(); }
        };
        p.BeginErrorReadLine();
        try
        {
            int id = ++s._nextId;
            s.Send(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id, method = "initialize",
                @params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "codecompass-selftest", version = "1" } },
            }));
            if (s.ReadResponse(id, handshakeTimeout) is null)
                throw new InvalidOperationException("the MCP server did not answer initialize: " + s.StderrTail);
            s.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    /// <summary>Call a tool and wait for its answer (sampling the server's memory throughout).</summary>
    public McpCallResult Call(string tool, IReadOnlyDictionary<string, object?> args, TimeSpan timeout)
    {
        int id = ++_nextId;
        _sampler.StartWindow();
        var sw = Stopwatch.StartNew();
        string? resp;
        try
        {
            Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = tool, arguments = args } }));
            resp = ReadResponse(id, timeout);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            resp = null; // the pipe closed under a dead server: an answer to report, like any other
        }
        sw.Stop();
        _sampler.Sample();
        if (resp is null)
            return new McpCallResult(Exited ? "the MCP server exited: " + StderrTail : $"no answer within {timeout.TotalMinutes:F0} min",
                                     sw.ElapsedMilliseconds, _sampler.WindowPeakMb, true, !Exited);
        using var doc = JsonDocument.Parse(resp); // ReadResponse returns only a line that parsed
        var rootEl = doc.RootElement;
        if (rootEl.TryGetProperty("error", out var err))
            return new McpCallResult(err.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : err.ToString(),
                                     sw.ElapsedMilliseconds, _sampler.WindowPeakMb, true, false);
        if (!rootEl.TryGetProperty("result", out var result))
            return new McpCallResult("an answer with neither a result nor an error: " + resp, sw.ElapsedMilliseconds, _sampler.WindowPeakMb, true, false);
        var sb = new StringBuilder();
        if (result.TryGetProperty("content", out var content))
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("text", out var tx)) sb.Append(tx.GetString());
        bool isError = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        return new McpCallResult(sb.ToString(), sw.ElapsedMilliseconds, _sampler.WindowPeakMb, isError, false);
    }

    private void Send(string json)
    {
        _p.StandardInput.WriteLine(json);
        _p.StandardInput.Flush();
    }

    // The response line carrying this id; null on timeout or the server exiting. Notifications (no id) are skipped.
    private string? ReadResponse(int id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            // A read left pending by a timed-out call is picked up again: a reader allows one read at a time, and a late
            // answer to that call is skipped below by its id.
            var read = _pendingRead ?? _p.StandardOutput.ReadLineAsync();
            _pendingRead = read;
            if (!read.Wait(left)) return null;
            _pendingRead = null;
            var line = read.Result;
            if (line is null) return null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.GetInt32() == id)
                    return line;
            }
            catch (JsonException) { /* not a protocol line */ }
        }
    }

    public void Dispose()
    {
        try { _p.StandardInput.Close(); } catch { }
        try { if (!_p.WaitForExit(10_000)) _p.Kill(entireProcessTree: true); } catch { }
        _sampler.Dispose();
        _p.Dispose();
    }
}
