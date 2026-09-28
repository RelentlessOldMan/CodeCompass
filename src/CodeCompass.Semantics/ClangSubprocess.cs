using System.Diagnostics;
using System.Text.Json;

namespace CodeCompass.Semantics;

/// <summary>
/// Runs a C/C++ find_references pass in a SHORT-LIVED CHILD PROCESS and marshals the result back as JSON.
///
/// <para><b>Why:</b> libclang's native TU memory is held by the process-global LLVM allocator and is not
/// returned to the OS while the process lives (disposing the managed analyzer frees nothing native). In a
/// long-lived MCP server, broad C/C++ queries therefore ratchet the process working set upward across
/// queries until the per-query ceiling. Doing the parse in a child that EXITS after each query hands all of
/// that native memory back to the OS immediately, so the long-lived parent stays flat - no server restart,
/// and it works the same whether the parent is the MCP server or anything else.</para>
///
/// <para>The worker IS the CLI exe (which already links this assembly) invoked as <c>clang-refs-worker</c>:
/// it reads a JSON request on stdin, runs the normal in-process analyzer, and writes a JSON response on
/// stdout. The per-query memory budget still applies inside the child, so a single query is bounded exactly
/// as before; the child just also releases on exit. On ANY failure (missing worker, crash, timeout, bad
/// JSON) the caller falls back to the in-process analyzer, so correctness never regresses.</para>
/// </summary>
public static class ClangSubprocess
{
    /// <summary>On by default; set CODECOMPASS_CPP_SUBPROCESS=0 to force the in-process analyzer.</summary>
    public static bool Enabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_SUBPROCESS");
            return !(v is "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Path to the worker exe (the CLI, next to whatever is running), or null if not found.</summary>
    public static string? WorkerExePath()
    {
        try
        {
            foreach (var name in new[] { "CodeCompass.Cli.exe", "CodeCompass.Cli" })
            {
                var p = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(p)) return p;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // DTOs for the stdin request / stdout response. Kept minimal and explicit so the wire format is stable.
    public sealed class RefRequest
    {
        public string Name { get; set; } = "";
        public int Max { get; set; } = 200;
        public List<string> Roots { get; set; } = new();
        public List<string> Candidates { get; set; } = new();
    }
    public sealed class LocDto
    {
        public string RelativePath { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public string LineText { get; set; } = "";
        public string Root { get; set; } = "";
    }
    public sealed class RefResponse
    {
        public List<LocDto> Locations { get; set; } = new();
        public int CandidateTus { get; set; }
        public int ParsedTus { get; set; }
        public List<string> UnresolvedIncludes { get; set; } = new();
        public bool MemoryStopped { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Run the C/C++ reference pass in a child process. Returns true and sets <paramref name="result"/>
    /// on success; false on any failure (caller should fall back to in-process). Never throws.</summary>
    public static bool TryFindReferences(string workerExe, IReadOnlyList<string> roots, string name,
        IReadOnlyCollection<string>? candidates, int max, int timeoutSeconds, out ClangCppAnalyzer.CppRefResult result)
    {
        result = default;
        Process? p = null;
        try
        {
            var req = new RefRequest
            {
                Name = name,
                Max = max,
                Roots = roots.ToList(),
                Candidates = candidates?.ToList() ?? new List<string>(),
            };

            var psi = new ProcessStartInfo
            {
                FileName = workerExe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("clang-refs-worker");

            p = Process.Start(psi);
            if (p is null) return false;

            // Drain stdout/stderr asynchronously BEFORE waiting, so a large result set can't deadlock the
            // child on a full pipe buffer.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            // Send the request, then close stdin so the worker knows the request is complete.
            JsonSerializer.Serialize(p.StandardInput.BaseStream, req, Json);
            p.StandardInput.BaseStream.Flush();
            p.StandardInput.Close();

            if (!p.WaitForExit(timeoutSeconds * 1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            if (p.ExitCode != 0) return false;

            var payload = outTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(payload)) return false;
            var resp = JsonSerializer.Deserialize<RefResponse>(payload, Json);
            if (resp is null) return false;

            var locs = resp.Locations
                .Select(l => new SemanticLocation(l.RelativePath, l.Line, l.Column, l.LineText, l.Root ?? ""))
                .ToList();
            result = new ClangCppAnalyzer.CppRefResult(locs, resp.CandidateTus, resp.ParsedTus,
                resp.UnresolvedIncludes ?? new List<string>(), resp.MemoryStopped);
            return true;
        }
        catch
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
            return false;
        }
    }

    /// <summary>Worker entry point (invoked as `CodeCompass.Cli.exe clang-refs-worker`). Reads a RefRequest
    /// from stdin, runs the in-process analyzer, writes a RefResponse to stdout. This process is disposable:
    /// it exits after one query, so the OS reclaims clang's native memory. Returns a process exit code.</summary>
    public static int RunWorkerMain()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        return RunWorkerCore(input, output);
    }

    /// <summary>Worker body over explicit streams (so it's unit-testable without spawning a process).</summary>
    public static int RunWorkerCore(Stream input, Stream output)
    {
        try
        {
            var req = JsonSerializer.Deserialize<RefRequest>(input, Json);
            if (req is null || string.IsNullOrEmpty(req.Name)) return 2;

            var roots = req.Roots.Count > 0 ? (IReadOnlyList<string>)req.Roots : new[] { Directory.GetCurrentDirectory() };
            var candidates = req.Candidates.Count > 0 ? req.Candidates : null;

            var res = new ClangCppAnalyzer(roots).FindReferencesDetailed(req.Name, candidates, req.Max);

            var resp = new RefResponse
            {
                CandidateTus = res.CandidateTus,
                ParsedTus = res.ParsedTus,
                MemoryStopped = res.MemoryStopped,
                UnresolvedIncludes = res.UnresolvedIncludes.ToList(),
                Locations = res.Locations.Select(l => new LocDto
                {
                    RelativePath = l.RelativePath, Line = l.Line, Column = l.Column, LineText = l.LineText, Root = l.Root,
                }).ToList(),
            };

            JsonSerializer.Serialize(output, resp, Json);
            output.Flush();
            return 0;
        }
        catch
        {
            return 3; // any failure -> nonzero exit; the parent falls back to in-process
        }
    }
}
