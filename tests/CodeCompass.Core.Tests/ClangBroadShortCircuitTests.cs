using System;
using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// A find_references over a PATHOLOGICALLY-BROAD C/C++ symbol (referenced across hundreds of candidate TUs)
// used to grind a semantic clang parse for minutes, hit the per-query memory budget partway, and fall back to
// lexical for the remainder anyway - pure wasted LATENCY for a result the fast lexical layer produces in ~2 s.
// (Field-observed on a large generated corpus over UNC: 1,405 candidates -> 253 parsed in 131 s -> 0 semantic
// refs kept, all-lexical answer.) The fix short-circuits: above CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES the
// semantic pass is SKIPPED up front (ParsedTus stays 0, TooManyCandidates set) so the lexical backfill answers
// fast, with honest disclosure. These tests pin that behaviour deterministically with a tiny limit - no real
// pressure and (for the skip) no clang parse at all.
[Collection("compaction-env")] // serialize the CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES env mutation
public class ClangBroadShortCircuitTests
{
    // Regression guard: the self-scan path (no candidate list - unit tests, or a CLI `refs` on an UNINDEXED
    // root) has NO lexical backfill, so the latency short-circuit must NOT fire there - skipping would turn a
    // slow-but-complete semantic answer into a bare zero (0 semantic + 0 lexical). Even with the limit set below
    // the candidate count, a self-scan keeps parsing semantically. The skip is reserved for the candidate-list
    // path (covered below), which always has a lexical net.
    [Fact]
    public void FindReferences_SelfScanOverLimit_DoesNotSkip()
    {
        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        for (int i = 0; i < 6; i++)
            repo.Write($"use_{i}.c", $"int hot(int);\nint use_{i}(int x){{ return hot(x); }}\n");

        var prevMax = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES");
        var prevQ = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB");
        var prevS = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB");
        try
        {
            // Limit (2) is below the ~7 self-scan candidates, yet self-scan must still parse (no lexical net).
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", "2");
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", "4000");
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", "4000");
            var r = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot"); // self-scan (no candidate list)

            Assert.False(r.TooManyCandidates, "self-scan has no lexical net, so it must not short-circuit");
            Assert.True(r.ParsedTus > 0, $"self-scan should keep parsing semantically, parsed {r.ParsedTus}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", prevMax);
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", prevQ);
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", prevS);
        }
    }

    [Fact]
    public void FindReferences_CandidatesUnderLimit_StillSemantic()
    {
        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        for (int i = 0; i < 6; i++)
            repo.Write($"use_{i}.c", $"int hot(int);\nint use_{i}(int x){{ return hot(x); }}\n");

        var prevMax = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES");
        var prevQ = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB");
        var prevS = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB");
        try
        {
            // A generous limit (and generous memory budgets) => the pass runs semantically, not skipped.
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", "1000");
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", "4000");
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", "4000");
            var r = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot");

            Assert.False(r.TooManyCandidates, "an under-limit candidate set must not short-circuit");
            Assert.True(r.ParsedTus > 0, $"the semantic pass should have parsed candidates, parsed {r.ParsedTus}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", prevMax);
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", prevQ);
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", prevS);
        }
    }

    // The MCP/CLI/subprocess path supplies the candidate list from the trigram index. The skip must fire on
    // that in-memory count WITHOUT resolving/statting the files (over UNC each stat is ~2 network round trips) -
    // proven here by pointing at paths that don't even exist: a skip that returns before ResolveFiles never
    // touches them, so CandidateTus reflects the supplied count and nothing is parsed.
    [Fact]
    public void FindReferences_CandidateListOverLimit_SkipsBeforeResolving()
    {
        using var repo = new TempRepo();
        var bogus = Enumerable.Range(0, 5)
            .Select(i => System.IO.Path.Combine(repo.Root, $"does_not_exist_{i}.c"))
            .ToList();

        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", "2");
            var r = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot", bogus);

            Assert.True(r.TooManyCandidates, "supplied candidate list over the limit should short-circuit");
            Assert.Equal(bogus.Count, r.CandidateTus);   // the in-memory supplied count, not a resolved count
            Assert.Equal(0, r.ParsedTus);
            Assert.Empty(r.Locations);
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", prev); }
    }

    // 0 disables the guard on the candidate-list path too (the path where it actually fires): a supplied list
    // well over any small count still parses semantically.
    [Fact]
    public void FindReferences_LimitZero_NeverSkips()
    {
        using var repo = new TempRepo();
        repo.Write("hot.c", "int hot(int x){ return x + 1; }\n");
        for (int i = 0; i < 6; i++)
            repo.Write($"use_{i}.c", $"int hot(int);\nint use_{i}(int x){{ return hot(x); }}\n");
        var candidates = System.IO.Directory.GetFiles(repo.Root, "*.c").ToList();

        var prevMax = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES");
        var prevQ = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB");
        var prevS = Environment.GetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", "0"); // 0 disables the guard
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", "4000");
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", "4000");
            var r = new ClangCppAnalyzer(repo.Root).FindReferencesDetailed("hot", candidates);
            Assert.False(r.TooManyCandidates, "limit 0 must disable the short-circuit entirely");
            Assert.True(r.ParsedTus > 0, $"with the guard disabled the full candidate list parses, parsed {r.ParsedTus}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", prevMax);
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_QUERY_MEM_MB", prevQ);
            Environment.SetEnvironmentVariable("CODECOMPASS_CPP_SESSION_MEM_MB", prevS);
        }
    }

    // The disclosure a too-broad skip earns must say WHY (the limit) and name the knob to override it - and NOT
    // also emit the generic "0/N parsed"/memory-stop lines, which would misread the deliberate skip as a failure.
    [Fact]
    public void CppCoverageBits_TooManyCandidates_EmitsDistinctDisclosure()
    {
        var bits = ReferenceMerge.CppCoverageBits(
            cppParsed: 0, cppCandidates: 1405, memoryStopped: false,
            System.Array.Empty<string>(), tooManyCandidates: true);

        var line = Assert.Single(bits);
        Assert.Contains("1,405", line);
        Assert.Contains("CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES", line);
        Assert.DoesNotContain("memory budget", line);   // not a memory stop
        Assert.DoesNotContain("parsed", line);          // not the generic parsed-shortfall line
    }
}
