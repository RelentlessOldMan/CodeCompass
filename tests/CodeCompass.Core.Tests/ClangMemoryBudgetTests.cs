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
}
