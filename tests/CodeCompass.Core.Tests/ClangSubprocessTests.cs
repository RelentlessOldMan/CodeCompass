using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// find_references for C/C++ is run in a short-lived CHILD PROCESS so libclang's native memory is reclaimed
// by the OS on exit (the long-lived server otherwise ratchets upward across queries). These tests cover the
// worker's serialization + result parity with the in-process analyzer, the graceful fallback on failure, and
// (when the CLI exe is present) a real end-to-end subprocess round-trip.
public class ClangSubprocessTests
{
    private static void WriteRepo(TempRepo repo)
    {
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        for (int i = 0; i < 6; i++)
            repo.Write($"use_{i}.c", $"int hot(int);\nint use_{i}(int x){{ return hot(x); }}\n");
    }

    // The worker body (over explicit streams, no process) must produce byte-for-byte the same references as
    // the in-process analyzer - same count, paths, lines, and coverage. This is the correctness contract that
    // lets the subprocess transparently replace the in-process call.
    [Fact]
    public void WorkerCore_MatchesInProcess()
    {
        using var repo = new TempRepo();
        WriteRepo(repo);

        var inproc = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot");

        var req = new ClangSubprocess.RefRequest { Name = "hot", Max = 200, Roots = { repo.Root } };
        using var inMs = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(req));
        using var outMs = new MemoryStream();
        int code = ClangSubprocess.RunWorkerCore(inMs, outMs);
        Assert.Equal(0, code);

        outMs.Position = 0;
        var resp = JsonSerializer.Deserialize<ClangSubprocess.RefResponse>(outMs.ToArray(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(resp);

        Assert.Equal(inproc.Locations.Count, resp!.Locations.Count);
        Assert.Equal(inproc.CandidateTus, resp.CandidateTus);
        Assert.Equal(inproc.ParsedTus, resp.ParsedTus);
        Assert.Equal(inproc.MemoryStopped, resp.MemoryStopped);
        // Same locations (order-independent).
        var a = inproc.Locations.Select(l => $"{l.RelativePath}:{l.Line}:{l.Column}").OrderBy(x => x).ToArray();
        var b = resp.Locations.Select(l => $"{l.RelativePath}:{l.Line}:{l.Column}").OrderBy(x => x).ToArray();
        Assert.Equal(a, b);
        Assert.True(resp.Locations.Count > 0, "expected to find references to hot()");
    }

    // An unknown symbol yields a clean empty result, exit 0 (not an error).
    [Fact]
    public void WorkerCore_UnknownSymbol_EmptyOk()
    {
        using var repo = new TempRepo();
        WriteRepo(repo);
        var req = new ClangSubprocess.RefRequest { Name = "nonexistent_symbol_zzz", Max = 200, Roots = { repo.Root } };
        using var inMs = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(req));
        using var outMs = new MemoryStream();
        Assert.Equal(0, ClangSubprocess.RunWorkerCore(inMs, outMs));
        outMs.Position = 0;
        var resp = JsonSerializer.Deserialize<ClangSubprocess.RefResponse>(outMs.ToArray(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(resp);
        Assert.Empty(resp!.Locations);
    }

    // Garbage on stdin must fail cleanly with a nonzero exit (so the parent falls back), never hang or throw.
    [Fact]
    public void WorkerCore_BadInput_NonZeroExit()
    {
        using var inMs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not json at all"));
        using var outMs = new MemoryStream();
        Assert.NotEqual(0, ClangSubprocess.RunWorkerCore(inMs, outMs));
    }

    // TryFindReferences against a bogus worker path must return false (caller then uses the in-process path),
    // never throw.
    [Fact]
    public void TryFindReferences_BogusWorker_ReturnsFalseNoThrow()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "definitely_not_a_real_worker_zzz.exe");
        var ok = ClangSubprocess.TryFindReferences(bogus, new[] { Path.GetTempPath() }, "hot", new[] { "x.c" }, 200, 10, out var r);
        Assert.False(ok);
        Assert.Null(r.Locations); // default(CppRefResult)
    }

    // End-to-end through the REAL child process, when the CLI exe is available (always true in the release
    // gate, which builds all projects before testing). Proves the actual spawn + stdin/stdout round-trip and
    // parity with the in-process analyzer. Soft-skips if the CLI hasn't been built (bare `dotnet test`).
    [Fact]
    public void RealSubprocess_RoundTrip_MatchesInProcess()
    {
        var cli = FindCliExe();
        if (cli is null) return; // CLI not built in this context; the gate builds it and exercises this for real

        using var repo = new TempRepo();
        WriteRepo(repo);
        var inproc = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot");

        var ok = ClangSubprocess.TryFindReferences(cli, new[] { repo.Root }, "hot", candidates: null, 200, 120, out var sub);
        Assert.True(ok, "the real subprocess worker should succeed");
        Assert.Equal(inproc.Locations.Count, sub.Locations.Count);
        Assert.Equal(inproc.ParsedTus, sub.ParsedTus);
        Assert.Equal(inproc.CandidateTus, sub.CandidateTus);
        Assert.True(sub.Locations.Count > 0);
    }

    private static string? FindCliExe()
    {
        // Walk up from the test's output dir to the repo root, then find the built CLI exe.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var cliBin = Path.Combine(dir.FullName, "src", "CodeCompass.Cli", "bin");
            if (Directory.Exists(cliBin))
            {
                var hit = Directory.EnumerateFiles(cliBin, "CodeCompass.Cli.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                return hit;
            }
        }
        return null;
    }
}
