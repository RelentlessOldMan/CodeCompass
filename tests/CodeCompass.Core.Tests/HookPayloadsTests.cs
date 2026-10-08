using System.Text.Json;
using CodeCompass.Core.Hooks;
using Xunit;

namespace CodeCompass.Core.Tests;

public class HookPayloadsTests
{
    [Fact]
    public void DenySearch_IsAValidPreToolUseDeny()
    {
        using var doc = JsonDocument.Parse(HookPayloads.DenySearch());
        var output = doc.RootElement.GetProperty("hookSpecificOutput");

        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        var reason = output.GetProperty("permissionDecisionReason").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("CodeCompass", reason);
    }

    [Fact]
    public void SessionContext_InjectsGuidance()
    {
        using var doc = JsonDocument.Parse(HookPayloads.SessionContext());
        var output = doc.RootElement.GetProperty("hookSpecificOutput");

        Assert.Equal("SessionStart", output.GetProperty("hookEventName").GetString());
        Assert.Contains("CodeCompass", output.GetProperty("additionalContext").GetString());
    }

    // Review P1-19: CodeCompass has no file-NAME search, so denying Glob was a dead end the agent escaped through a
    // shell command. Only Grep (content search) is redirected, and the denial says what to use for each need.
    [Fact]
    public void Hook_RedirectsGrepOnly_AndPointsGlobAtFileNames()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "plugin", "hooks", "hooks.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var hooks = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "plugin", "hooks", "hooks.json")));
        var matcher = hooks.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("matcher").GetString();
        Assert.Equal("Grep", matcher);

        using var doc = JsonDocument.Parse(HookPayloads.DenySearch());
        var reason = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecisionReason").GetString()!;
        Assert.Contains("Glob for file-NAME", reason);
    }
}
// The Grep hook redirects only the searches CodeCompass can answer: a literal pattern inside the workspace. A regex
// (alternation, classes, quantifiers, anchors, \w-style escapes, multiline) or a path outside the workspace has no
// CodeCompass equivalent, so denying it only pushed the agent to several literal searches or to grep through a shell.
public class GrepRedirectTests
{
    private static string Payload(string pattern, string? path = null, bool multiline = false, string cwd = @"C:\repo")
    {
        var input = new Dictionary<string, object?> { ["pattern"] = pattern };
        if (path is not null) input["path"] = path;
        if (multiline) input["multiline"] = true;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse", ["tool_name"] = "Grep", ["cwd"] = cwd, ["tool_input"] = input,
        });
    }

    [Theory]
    [InlineData("FindReferences")]
    [InlineData("image.size")]                 // a bare dot: the literal hit is what was meant
    [InlineData(@"Foo\(bar\)")]                 // escaped punctuation is literal
    [InlineData("could not open file")]
    public void LiteralPatternInsideTheWorkspace_IsRedirected(string pattern)
    {
        Assert.True(HookPayloads.ShouldRedirectGrep(Payload(pattern)));
        Assert.True(HookPayloads.ShouldRedirectGrep(Payload(pattern, path: @"C:\repo\src")));
        Assert.True(HookPayloads.ShouldRedirectGrep(Payload(pattern, path: "src")));
    }

    [Theory]
    [InlineData("diff|image.size|object.file")]
    [InlineData("foo.*bar")]
    [InlineData("colou?r")]
    [InlineData("[A-Z]Index")]
    [InlineData("^using ")]
    [InlineData("return;$")]
    [InlineData(@"\bName\b")]
    [InlineData(@"\w+Async")]
    [InlineData("a{2,}")]
    [InlineData("(get|set)Value")]
    public void RegexPattern_PassesThroughToGrep(string pattern)
        => Assert.False(HookPayloads.ShouldRedirectGrep(Payload(pattern)));

    [Fact]
    public void Multiline_PassesThroughToGrep()
        => Assert.False(HookPayloads.ShouldRedirectGrep(Payload("Name", multiline: true)));

    [Theory]
    [InlineData(@"C:\temp")]
    [InlineData(@"C:\repo-other\src")]
    [InlineData(@"..\elsewhere")]
    public void PathOutsideTheWorkspace_PassesThroughToGrep(string path)
        => Assert.False(HookPayloads.ShouldRedirectGrep(Payload("Name", path: path)));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void UnreadablePayload_KeepsTheRedirect(string payload)
        => Assert.True(HookPayloads.ShouldRedirectGrep(payload));

    [Fact]
    public void DenyReason_SaysWhatStillGoesToGrep()
    {
        using var doc = JsonDocument.Parse(HookPayloads.DenySearch());
        var reason = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecisionReason").GetString()!;
        Assert.Contains("regex", reason);
    }
}
