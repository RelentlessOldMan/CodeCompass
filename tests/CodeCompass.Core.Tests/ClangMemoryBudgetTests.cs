using System;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// find_references over the C/C++ semantic layer parses candidate TUs; clang's native TU memory is not
// returned to the OS as the query walks candidates, so a broad query over many ordinary sources would grow
// unbounded (a common symbol across 800+ files climbed past 9 GB on a real tree). The per-query memory
// budget stops the semantic pass early and discloses the partial coverage, bounding peak regardless of
// candidate count. This forces the stop deterministically with a tiny budget - no real pressure needed.
[Collection("compaction-env")] // serialize the CODECOMPASS_CPP_QUERY_MEM_MB env mutation
public class ClangMemoryBudgetTests
{
    [Fact]
    public void FindReferences_StopsAtMemoryBudget_Partial()
    {
        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        for (int i = 0; i < 12; i++)
            repo.Write($"use_{i}.c", $"int hot(int);\nint use_{i}(int x){{ return hot(x); }}\n");

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB");
        try
        {
            // 1 MB budget: the working set grows past it within the first parse or two, so the pass stops
            // long before all candidates are parsed.
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", "1");
            var r = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot");

            Assert.True(r.CandidateTus >= 2, $"expected multiple candidate TUs, got {r.CandidateTus}");
            Assert.True(r.MemoryStopped, "the semantic pass should stop at the 1 MB budget");
            Assert.True(r.ParsedTus < r.CandidateTus, $"a stopped pass parses fewer TUs than candidates ({r.ParsedTus} < {r.CandidateTus})");
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", prev); }
    }

    // Regression guard from the 1.0.176 real-tree field report: the memory FIX must not silently degrade to
    // lexical-only. v1.0.176's absolute session ceiling clamped to 3 GB regardless of box RAM, so on a big box
    // the pass parsed 0 candidates and returned all-lexical while STILL keeping peak RSS flat - a flat-RSS check
    // alone would have passed it. This asserts the complement: with a generous budget a cold pass parses ALL
    // candidates (coverage stays high), so a future change can't regress coverage to 0/N unnoticed.
    [Fact]
    public void FindReferences_GenerousBudget_ParsesAllCandidates()
    {
        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        for (int i = 0; i < 12; i++)
            repo.Write($"use_{i}.c", $"int hot(int);\nint use_{i}(int x){{ return hot(x); }}\n");

        var prevQ = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB");
        var prevS = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB");
        try
        {
            // Generous per-query and per-session budgets: nothing should trip on 13 tiny TUs.
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", "4000");
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", "4000");
            var r = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot");

            Assert.True(r.CandidateTus >= 2, $"expected multiple candidate TUs, got {r.CandidateTus}");
            Assert.False(r.MemoryStopped, "a generous budget must not stop the pass on tiny TUs");
            Assert.Equal(r.CandidateTus, r.ParsedTus); // full coverage: every candidate parsed
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", prevQ);
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", prevS);
        }
    }

    // A session ceiling below the 512 MB per-query growth floor must not throw: the growth budget's
    // Clamp(value, floor, ceiling) would have floor > ceiling and throw ArgumentException. Guards the
    // gbFloor = Min(512 MB, absCeiling) fix. (Regression: found validating the 179 CLI refs fix - a
    // CODECOMPASS_CPP_SESSION_MEM_MB below 512 crashed find_references outright.)
    [Fact]
    public void FindReferences_TinySessionCeiling_DoesNotThrow()
    {
        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        repo.Write("use.c", "int hot(int);\nint use(int x){ return hot(x); }\n");

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", "100"); // below the 512 MB floor
            var ex = Record.Exception(() => new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot"));
            Assert.Null(ex); // must not throw; the pass may stop early, but it returns a result
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", prev); }
    }
}
