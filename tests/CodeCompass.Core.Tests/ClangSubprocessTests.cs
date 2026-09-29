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

    // The parent must decode the child's stdout as UTF-8 (pinned via StandardOutputEncoding), not the console
    // codepage - otherwise non-ASCII in a returned LineText/path is mojibake (and a corrupted path breaks the
    // lexical dedup key). Round-trips a non-ASCII source line through the real child and asserts it survives.
    [Fact]
    public void RealSubprocess_NonAscii_RoundTripsIntact()
    {
        var cli = FindCliExe();
        if (cli is null) return;

        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        // A reference to hot() on a line carrying non-ASCII text (accented + a non-Latin glyph).
        repo.Write("use.c", "int hot(int);\nint use(int x){ return hot(x); } // café ☃ éü\n");

        var ok = ClangSubprocess.TryFindReferences(cli, new[] { repo.Root }, "hot", candidates: null, 200, 120, out var sub);
        Assert.True(ok);
        var useHit = sub.Locations.FirstOrDefault(l => l.RelativePath.EndsWith("use.c"));
        Assert.False(string.IsNullOrEmpty(useHit.RelativePath), "expected a hit in use.c");
        Assert.Contains("café", useHit.LineText);
        Assert.Contains("☃", useHit.LineText); // snowman survived (not mojibake)
    }

    // A HARD worker failure (nonzero exit / broken pipe) is usually an uncatchable LLVM OOM abort() while
    // parsing a pathological giant TU: the graceful per-query memory stop never runs because a single huge
    // allocation dies mid-parse. The parent must NOT re-run that parse in-process - it would re-trigger the
    // abort in the CLI process (or the long-lived MCP server), the exact crash the child isolates, and surface
    // to the user as a SILENT 0 refs + nonzero exit. Instead it returns an INCOMPLETE pass so the caller's
    // lexical backfill + coverage disclosure produce an honest answer. (Found going ham on 'death', 2026-09-29.)
    [Fact]
    public void ContainedFailure_OomStderr_IsIncompleteAndMemoryStopped()
    {
        var cands = new[] { "a.c", "b.c", "c.c" };
        var r = ClangSubprocess.ContainedFailure(cands, "LLVM ERROR: out of memory\nBuffer allocation failed\n");
        Assert.Empty(r.Locations);
        Assert.Equal(0, r.ParsedTus);
        Assert.Equal(cands.Length, r.CandidateTus);
        Assert.True(r.MemoryStopped, "an OOM signature must mark the pass memory-stopped so the caveat names the ceiling knob");
        Assert.True(SemanticCoverage.IsCppPassIncomplete(r.MemoryStopped, r.ParsedTus, r.CandidateTus, r.UnresolvedIncludes.Count),
            "a contained crash must count as an incomplete pass so lexical backfill runs (never a silent 0)");
        var bits = ReferenceMerge.CppCoverageBits(r.ParsedTus, r.CandidateTus, r.MemoryStopped, r.UnresolvedIncludes);
        Assert.Contains(bits, b => b.Contains("candidate C/C++ file(s) parsed"));
        Assert.Contains(bits, b => b.Contains("memory budget"));
    }

    // A non-OOM hard failure (e.g. a broken pipe with no OOM text) is still contained as incomplete, but must
    // NOT falsely blame the memory ceiling - the honest "0/N parsed" bit carries it.
    [Fact]
    public void ContainedFailure_NonOomCrash_IncompleteWithoutMemoryClaim()
    {
        var cands = new[] { "a.c", "b.c" };
        var r = ClangSubprocess.ContainedFailure(cands, "worker died: pipe closed");
        Assert.Equal(0, r.ParsedTus);
        Assert.Equal(2, r.CandidateTus);
        Assert.False(r.MemoryStopped);
        Assert.True(SemanticCoverage.IsCppPassIncomplete(r.MemoryStopped, r.ParsedTus, r.CandidateTus, 0)); // 0 < 2
        var bits = ReferenceMerge.CppCoverageBits(r.ParsedTus, r.CandidateTus, r.MemoryStopped, r.UnresolvedIncludes);
        Assert.Contains(bits, b => b.Contains("candidate C/C++ file(s) parsed"));
        Assert.DoesNotContain(bits, b => b.Contains("memory budget"));
    }

    // With no candidate count to make 0<N true, incompleteness must still be forced so lexical backfill runs.
    [Fact]
    public void ContainedFailure_NoCandidates_StillIncomplete()
    {
        var r = ClangSubprocess.ContainedFailure(null, "worker died");
        Assert.True(r.MemoryStopped);
        Assert.True(SemanticCoverage.IsCppPassIncomplete(r.MemoryStopped, r.ParsedTus, r.CandidateTus, 0));
    }

    [Theory]
    [InlineData("LLVM ERROR: out of memory", true)]
    [InlineData("terminate called after throwing an instance of 'std::bad_alloc'", true)]
    [InlineData("Buffer allocation failed", true)]
    [InlineData("clang: warning: some benign message", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOomSignature_DetectsNativeOom(string? stderr, bool expected)
        => Assert.Equal(expected, ClangSubprocess.IsOomSignature(stderr));

    // End-to-end: a REAL worker that exits nonzero must be CONTAINED (ok==true, incomplete result), NOT
    // reported as false (which would send the caller into the crashing in-process retry). An empty name forces
    // the worker's nonzero exit deterministically without needing to actually OOM libclang. Soft-skips if the
    // CLI exe isn't built (bare `dotnet test`); the release gate builds it and runs this for real.
    [Fact]
    public void TryFindReferences_WorkerNonZeroExit_ContainedAsIncomplete()
    {
        var cli = FindCliExe();
        if (cli is null) return;
        var cands = new[] { "x.c", "y.c", "z.c" };
        var ok = ClangSubprocess.TryFindReferences(cli, new[] { Path.GetTempPath() }, "", cands, 200, 60, out var r);
        Assert.True(ok, "a worker crash must be contained (return true with an incomplete result), not fall through to in-process");
        Assert.Empty(r.Locations);
        Assert.Equal(0, r.ParsedTus);
        Assert.Equal(cands.Length, r.CandidateTus);
        Assert.True(SemanticCoverage.IsCppPassIncomplete(r.MemoryStopped, r.ParsedTus, r.CandidateTus, 0));
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
