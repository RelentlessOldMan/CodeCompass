using System;
using System.IO;
using System.Text;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// Deterministic, native-free coverage for the C/C++ semantics helpers whose only prior exercise was a real
// libclang parse (flaky/heavy): the honest coverage-caveat formatting, the worker env clamps, and the
// worker's bad-request exit-code contract.
public class SemanticsUnitTests
{
    private sealed class EnvScope : IDisposable
    {
        private readonly string _key;
        private readonly string? _old;
        public EnvScope(string key, string? val)
        {
            _key = key;
            _old = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, val);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_key, _old);
    }

    // ---- ReferenceMerge.CppCoverageBits (honest disclosure formatting) ----

    [Fact]
    public void CppCoverageBits_FullyCovered_IsEmpty()
    {
        // parsed == candidates, no memory stop, no unresolved includes => nothing to disclose.
        var bits = ReferenceMerge.CppCoverageBits(3, 3, memoryStopped: false, Array.Empty<string>());
        Assert.Empty(bits);
    }

    [Fact]
    public void CppCoverageBits_UnresolvedOverFive_TruncatesWithMoreSuffix()
    {
        var inc = new[] { "a.h", "b.h", "c.h", "d.h", "e.h", "f.h", "g.h" }; // 7
        var bits = ReferenceMerge.CppCoverageBits(3, 3, memoryStopped: false, inc);
        var line = Assert.Single(bits);
        Assert.Contains("7 unresolved #include(s)", line);
        Assert.Contains("a.h, b.h, c.h, d.h, e.h", line); // first 5, in order
        Assert.Contains("+2 more", line);                 // 7 - 5
        Assert.DoesNotContain("f.h", line);               // the 6th is folded into "+2 more"
    }

    [Fact]
    public void CppCoverageBits_UnresolvedExactlyFive_NoMoreSuffix()
    {
        var inc = new[] { "a.h", "b.h", "c.h", "d.h", "e.h" };
        var line = Assert.Single(ReferenceMerge.CppCoverageBits(3, 3, false, inc));
        Assert.Contains("5 unresolved #include(s)", line);
        Assert.DoesNotContain("more", line);
    }

    [Fact]
    public void CppCoverageBits_ReportsParsedShortfall_AndMemoryStop()
    {
        var bits = ReferenceMerge.CppCoverageBits(2, 5, memoryStopped: true, Array.Empty<string>());
        Assert.Contains(bits, b => b.Contains("2") && b.Contains("5") && b.Contains("parsed"));
        Assert.Contains(bits, b => b.Contains("memory budget"));
    }

    // ---- ClangSubprocess.StallSeconds (env parse + clamp; a no-progress window, not a total time limit) ----

    [Theory]
    [InlineData(null, 600)]       // unset -> default
    [InlineData("", 600)]         // garbage -> default
    [InlineData("abc", 600)]      // garbage -> default
    [InlineData("0", 600)]        // non-positive -> default
    [InlineData("-5", 600)]       // negative -> default
    [InlineData("120", 120)]      // in range -> as-is
    [InlineData("999999", 86400)] // above the ceiling -> clamped down
    public void StallSeconds_ParsesAndClamps(string? value, int expected)
    {
        using var _ = new EnvScope("CODECOMPASS_CPP_WORKER_STALL_SEC", value);
        using var __ = new EnvScope("CODECOMPASS_CPP_WORKER_TIMEOUT_SEC", null);
        Assert.Equal(expected, ClangSubprocess.StallSeconds());
    }

    [Fact]
    public void StallSeconds_HonorsLegacyTimeoutVariable()
    {
        using var _ = new EnvScope("CODECOMPASS_CPP_WORKER_STALL_SEC", null);
        using var __ = new EnvScope("CODECOMPASS_CPP_WORKER_TIMEOUT_SEC", "900");
        Assert.Equal(900, ClangSubprocess.StallSeconds());
    }

    // ---- ClangSubprocess.Enabled (opt-out env) ----

    [Theory]
    [InlineData(null, true)]   // default on
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("OFF", false)] // case-insensitive
    public void Enabled_HonorsOptOut(string? value, bool expected)
    {
        using var _ = new EnvScope("CODECOMPASS_CPP_SUBPROCESS", value);
        Assert.Equal(expected, ClangSubprocess.Enabled);
    }

    // ---- ClangSubprocess.RunWorkerCore (bad-request exit-code contract) ----

    [Fact]
    public void RunWorkerCore_ValidJsonEmptyName_ReturnsTwo_AndWritesNothing()
    {
        // A well-formed request with an empty symbol name is a BAD REQUEST (exit 2), distinct from the
        // catch-all worker-crash code (3). The parent relies on that distinction to contain-vs-fallback.
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""{"Name":"","Max":100,"Roots":[],"Candidates":[]}"""));
        using var output = new MemoryStream();
        Assert.Equal(2, ClangSubprocess.RunWorkerCore(input, output));
        Assert.Equal(0, output.Length); // nothing serialized on the bad-request path
    }

    [Fact]
    public void RunWorkerCore_NullRequest_ReturnsTwo()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("null"));
        using var output = new MemoryStream();
        Assert.Equal(2, ClangSubprocess.RunWorkerCore(input, output));
    }

    [Fact]
    public void RunWorkerCore_MalformedJson_IsABadRequest()
    {
        // Non-JSON is rejected before any native work, so it's the RETRYABLE bad-request exit - the parent may run it
        // in-process. (Exit 3 is reserved for failures DURING a parse, which the parent contains instead.)
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("this is not json {"));
        using var output = new MemoryStream();
        Assert.Equal(ClangSubprocess.BadRequestExit, ClangSubprocess.RunWorkerCore(input, output));
    }
}
