using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// In-process unit coverage for the find_references / find_callees DISCLOSURE wording (the v1.0.215/216 #if work).
// The wording is exercised end-to-end by the Cli_* subprocess tests, but coverlet instruments only the test host
// - a child CodeCompass.Cli.exe process is invisible to it - so these pure string builders showed 0% covered and,
// more importantly, the FileList ">5 -> +N more" truncation (which the single-file CLI tests never reach) went
// unpinned. These are allocation-cheap, deterministic, and need no repo.
public class ReferenceMergeNoteTests
{
    [Fact]
    public void CSharpConditionalNote_EmptyWhenNoConditionalFiles()
        => Assert.Equal("", ReferenceMerge.CSharpConditionalNote(System.Array.Empty<string>()));

    [Fact]
    public void CSharpConditionalNote_NamesTheFile_AndFlagsIncomplete()
    {
        var note = ReferenceMerge.CSharpConditionalNote(new[] { "src/Login.cs" });
        Assert.Contains("C# coverage INCOMPLETE", note);
        Assert.Contains("#if/#elif", note);
        Assert.Contains("src/Login.cs", note);        // NAMES the conditional file, not a generic warning
        Assert.Contains("shown lexically", note);
    }

    [Fact]
    public void CSharpConditionalNote_TruncatesFileListPastFive()
    {
        var files = Enumerable.Range(1, 7).Select(i => $"f{i}.cs").ToArray();
        var note = ReferenceMerge.CSharpConditionalNote(files);
        Assert.Contains("f1.cs", note);
        Assert.Contains("f5.cs", note);
        Assert.Contains("+2 more", note);              // only the first 5 are named; the remainder summarized
        Assert.DoesNotContain("f6.cs", note);
    }

    [Fact]
    public void CSharpConditionalCalleesNote_EmptyWhenNoConditionalFiles()
        => Assert.Equal("", ReferenceMerge.CSharpConditionalCalleesNote(System.Array.Empty<string>(), recoveredCount: 0));

    [Fact]
    public void CSharpConditionalCalleesNote_SaysRecovered_WhenSomeResolved()
    {
        var note = ReferenceMerge.CSharpConditionalCalleesNote(new[] { "Login.cs" }, recoveredCount: 3);
        Assert.Contains("Login.cs", note);
        Assert.Contains("recovered by name below", note);   // points to the segregated section
    }

    [Fact]
    public void CSharpConditionalCalleesNote_SaysMayBeMissing_WhenNoneResolved()
    {
        var note = ReferenceMerge.CSharpConditionalCalleesNote(new[] { "Login.cs" }, recoveredCount: 0);
        Assert.Contains("Login.cs", note);
        Assert.Contains("may be missing", note);            // honest: nothing resolved, so some calls may be missing
    }

    [Fact]
    public void CSharpInactiveCalleesHeader_StatesCount_AndVerifyHint()
    {
        var header = ReferenceMerge.CSharpInactiveCalleesHeader(4);
        Assert.Contains("4 more callee(s)", header);
        Assert.Contains("resolved by NAME", header);
        Assert.Contains("verify with find_definition", header);
    }
}
