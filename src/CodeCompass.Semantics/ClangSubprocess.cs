using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeCompass.Core.Diagnostics;

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
        public bool TooManyCandidates { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>True when the worker's stderr shows an uncatchable NATIVE out-of-memory: LLVM's fatal
    /// allocation handler (or a libc bad_alloc) calls abort(), so a single pathological giant TU can die
    /// mid-parse BEFORE the graceful per-query memory stop ever runs. Used to mark a contained failure as
    /// memory-stopped so the coverage caveat points at the right ceiling knob.</summary>
    public static bool IsOomSignature(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("LLVM ERROR", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("bad_alloc", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Buffer allocation failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The result to use when the isolated worker FAILED HARD (nonzero exit, timeout, or a broken pipe
    /// from the child abort()ing) - most often an uncatchable LLVM OOM while parsing a pathological giant TU.
    /// The parent must NOT re-run that parse in-process: it would re-trigger the abort in the caller (the CLI
    /// process, or the long-lived MCP server) - the very crash the child isolates - which surfaces to a user as
    /// a SILENT 0 references + nonzero exit. Instead we return an INCOMPLETE pass (0 parsed of N candidates,
    /// memory-stopped on an OOM signature) so the caller's lexical backfill + coverage disclosure produce an
    /// honest, non-empty answer. (Found going ham on the 'death' corpus, 2026-09-29.)</summary>
    public static ClangCppAnalyzer.CppRefResult ContainedFailure(IReadOnlyCollection<string>? candidates, string? stderr)
    {
        int cand = candidates?.Count ?? 0;
        // 0 parsed < N candidates already marks the pass incomplete; when we have no candidate count, set
        // memoryStopped so IsCppPassIncomplete is still true and the lexical backfill runs regardless.
        bool memStopped = IsOomSignature(stderr) || cand == 0;
        return new ClangCppAnalyzer.CppRefResult(
            Array.Empty<SemanticLocation>(), cand, 0, Array.Empty<string>(), memStopped);
    }

    /// <summary>Run the C/C++ reference pass in a child process. Returns true and sets <paramref name="result"/>
    /// on success; false on any failure (caller should fall back to in-process). Never throws.</summary>
    public static bool TryFindReferences(string workerExe, IReadOnlyList<string> roots, string name,
        IReadOnlyCollection<string>? candidates, int max, int timeoutSeconds, out ClangCppAnalyzer.CppRefResult result,
        System.Threading.CancellationToken ct = default)
    {
        result = default;
        Process? p = null;
        Task<string>? outTask = null, errTask = null;
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
                // The child emits UTF-8 (JsonSerializer to the raw stdout stream). Pin the parent's decoders
                // to UTF-8 (no BOM) so a non-UTF-8 console codepage can't mojibake non-ASCII paths/line text
                // (which would also break the lexical dedup key -> double-counted refs). stdin is written as
                // raw bytes below, but pin its encoding too so the contract is explicit either way.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("clang-refs-worker");

            p = Process.Start(psi);
            if (p is null) { Log.Global.Warn("clang subprocess: Process.Start returned null; using in-process fallback"); return false; }

            // Teardown cancellation: a shutdown/re-point kills the child promptly so a long C/C++ parse doesn't
            // keep the read lock (blocking the re-point's write lock) until the per-query timeout. The caller
            // re-checks the token after this returns and surfaces the cancellation; here we just stop the work.
            // (If already cancelled, Register runs the kill synchronously.)
            using var cancelKill = ct.CanBeCanceled
                ? ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } })
                : default;

            // Drain stdout/stderr asynchronously BEFORE waiting, so a large result set can't deadlock the
            // child on a full pipe buffer. stdout is read with a byte/char ceiling: the result set is already
            // bounded by `max`, but a rogue/pathological child must not be able to balloon the long-lived
            // parent's managed heap via an unbounded ReadToEnd.
            outTask = ReadCappedAsync(p.StandardOutput, MaxResponseChars);
            errTask = p.StandardError.ReadToEndAsync();

            // Send the request on a task guarded by the timeout, then close stdin. A synchronous write has no
            // timeout: if the child dies/exits BEFORE draining a large request (many candidate paths can exceed
            // the OS stdin pipe buffer), the write would block forever and WaitForExit below would never be
            // reached. Guarding the write bounds that hang.
            var writeTask = Task.Run(() =>
            {
                try
                {
                    JsonSerializer.Serialize(p.StandardInput.BaseStream, req, Json);
                    p.StandardInput.BaseStream.Flush();
                    p.StandardInput.Close();
                }
                catch { /* child gone/broken pipe: the exit-code / empty-output path handles it */ }
            });
            if (!writeTask.Wait(timeoutSeconds * 1000))
            {
                KillAndReap(p, outTask, errTask);
                Log.Global.Warn($"clang subprocess: timed out after {timeoutSeconds}s WRITING the request to '{name}' ({req.Candidates.Count} candidates); CONTAINED as an incomplete C/C++ pass (lexical backfill will cover) - NOT retried in-process");
                result = ContainedFailure(candidates, null);
                return true;
            }

            if (!p.WaitForExit(timeoutSeconds * 1000))
            {
                KillAndReap(p, outTask, errTask);
                Log.Global.Warn($"clang subprocess: timed out after {timeoutSeconds}s on '{name}' ({req.Candidates.Count} candidates); CONTAINED as an incomplete C/C++ pass (lexical backfill will cover) - NOT retried in-process (raise CODECOMPASS_CPP_WORKER_TIMEOUT_SEC)");
                result = ContainedFailure(candidates, null);
                return true;
            }
            p.WaitForExit(); // parameterless: ensures the async stdout/stderr readers have fully flushed
            var payload = outTask.GetAwaiter().GetResult();
            var errText = errTask.GetAwaiter().GetResult(); // observe stderr so it's never an unobserved task
            if (p.ExitCode != 0)
            {
                // The worker ran and died. Overwhelmingly this is an uncatchable LLVM OOM abort() on a
                // pathological giant TU. Re-running that parse in-process would re-trigger the abort in THIS
                // process (CLI or the long-lived MCP server), surfacing as a silent 0 refs + nonzero exit - so
                // contain it as an incomplete pass and let the caller's lexical backfill + disclosure answer.
                Log.Global.Warn($"clang subprocess: exit {p.ExitCode} on '{name}'; CONTAINED as an incomplete C/C++ pass (lexical backfill will cover) - NOT retried in-process. stderr: {Tail(errText)}");
                result = ContainedFailure(candidates, errText);
                return true;
            }
            if (string.IsNullOrWhiteSpace(payload)) { Log.Global.Warn($"clang subprocess: empty output on '{name}'; using in-process fallback. stderr: {Tail(errText)}"); return false; }
            var resp = JsonSerializer.Deserialize<RefResponse>(payload, Json);
            if (resp is null) return false;

            var locs = resp.Locations
                .Select(l => new SemanticLocation(l.RelativePath, l.Line, l.Column, l.LineText, l.Root ?? ""))
                .ToList();
            result = new ClangCppAnalyzer.CppRefResult(locs, resp.CandidateTus, resp.ParsedTus,
                resp.UnresolvedIncludes ?? new List<string>(), resp.MemoryStopped, resp.TooManyCandidates);
            return true;
        }
        catch (Exception ex)
        {
            // Kill the child (if any) and reap the reader tasks so they complete instead of lingering as
            // orphaned reads against a dead pipe (a potential unobserved-task fault on a busy server).
            if (p is not null) KillAndReap(p, outTask, errTask);
            if (p is null)
            {
                // The worker never started (missing/locked exe, bad ProcessStartInfo). No parse was attempted,
                // so there's no native crash to contain - let the caller use the in-process analyzer.
                Log.Global.Warn($"clang subprocess: {ex.GetType().Name} starting worker on '{name}'; using in-process fallback: {ex.Message}");
                return false;
            }
            // The worker HAD started, then the pipe/wait threw - almost always the child abort()ing hard (LLVM
            // OOM) mid-request. Re-running that parse in-process would re-trigger the abort HERE, so contain it.
            Log.Global.Warn($"clang subprocess: {ex.GetType().Name} after worker start on '{name}'; CONTAINED as an incomplete C/C++ pass (lexical backfill will cover) - NOT retried in-process: {ex.Message}");
            result = ContainedFailure(candidates, ex.Message);
            return true;
        }
        finally
        {
            // The Process object holds a Win32 process handle (and, once redirected, the pipe handles). Without
            // this, every C/C++ query in the long-lived MCP server leaks a kernel handle until finalization.
            p?.Dispose();
        }
    }

    /// <summary>Ceiling on the child's stdout the parent will buffer. The result set is bounded by `max`, so this
    /// only trips on a pathological/rogue child; a truncated payload fails JSON parse -> in-process fallback.</summary>
    private const int MaxResponseChars = 64 * 1024 * 1024;

    /// <summary>Read a stream reader to end but stop buffering past <paramref name="maxChars"/> (still draining the
    /// pipe so the child isn't blocked), so a hostile/huge child response can't OOM the long-lived parent.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, int maxChars)
    {
        var sb = new StringBuilder();
        var buf = new char[16384];
        int n;
        while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
        {
            if (sb.Length < maxChars) sb.Append(buf, 0, Math.Min(n, maxChars - sb.Length));
            // else: keep draining to EOF but discard, so the child can exit rather than block on a full pipe.
        }
        return sb.ToString();
    }

    /// <summary>Kill the child (whole tree) and wait briefly for it to exit and for the async pipe readers to
    /// finish, so nothing is left running against a dead process. Best-effort; never throws.</summary>
    private static void KillAndReap(Process p, params Task?[] readers)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(2000); } catch { }
        try
        {
            var live = readers.Where(t => t is not null).Cast<Task>().ToArray();
            if (live.Length > 0) Task.WaitAll(live, 2000);
        }
        catch { /* a reader faulting on the killed pipe is expected; observe and ignore */ }
    }

    private static string Tail(string? s, int max = 300)
    {
        if (string.IsNullOrEmpty(s)) return "(none)";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : "..." + s[^max..];
    }

    /// <summary>Per-query worker timeout in seconds (env CODECOMPASS_CPP_WORKER_TIMEOUT_SEC, default 300),
    /// clamped to a sane range.</summary>
    public static int TimeoutSeconds()
    {
        var v = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_WORKER_TIMEOUT_SEC");
        return int.TryParse(v, out var s) && s > 0 ? Math.Clamp(s, 5, 3600) : 300;
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
                TooManyCandidates = res.TooManyCandidates,
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
