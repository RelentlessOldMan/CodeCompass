using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// The single "is the C/C++ pass incomplete?" predicate that both the CLI (CmdRefs) and the MCP handler use
// to decide whether to backfill lexical refs. This logic was duplicated and drifted twice (the b5 fix, then
// the unresolved-include case); these tests pin every branch so it can't silently regress in either caller.
public class CppCoverageIncompleteTests
{
    [Theory]
    // memoryStopped, parsed, candidates, unresolved  => expected incomplete
    [InlineData(false, 10, 10, 0, false)]   // fully parsed, no unresolved, not stopped => COMPLETE
    [InlineData(true, 10, 10, 0, true)]     // memory-stopped => incomplete
    [InlineData(false, 7, 10, 0, true)]     // some TUs didn't parse => incomplete
    [InlineData(false, 10, 10, 1, true)]    // unresolved include, yet parsed==candidate => incomplete (the false-zero case)
    [InlineData(false, 10, 10, 5, true)]    // multiple unresolved includes => incomplete
    [InlineData(true, 3, 10, 2, true)]      // everything wrong => incomplete
    [InlineData(false, 0, 0, 0, false)]     // no candidates at all => not incomplete (nothing to backfill)
    public void IsCppPassIncomplete_AllBranches(bool memStopped, int parsed, int candidates, int unresolved, bool expected)
        => Assert.Equal(expected, SemanticCoverage.IsCppPassIncomplete(memStopped, parsed, candidates, unresolved));
}
