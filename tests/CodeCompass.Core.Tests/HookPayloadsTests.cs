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
// The Grep hook redirects a search to CodeCompass only when search_code gives the same answer: a non-empty literal
// pattern, over the whole indexed project, with no Grep option search_code lacks. Anything else - a regex, a
// subfolder or file, a path outside the project, a glob/type filter, context lines, a count, no index yet - goes to
// Grep, because denying it only pushed the agent to several literal searches or to grep through a shell.
public class GrepRedirectTests
{
    private const string Root = @"C:\repo";

    private static string Payload(string pattern, string? path = null, string cwd = Root,
                                  Dictionary<string, object?>? extra = null)
    {
        var input = new Dictionary<string, object?> { ["pattern"] = pattern };
        if (path is not null) input["path"] = path;
        if (extra is not null) foreach (var kv in extra) input[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse", ["tool_name"] = "Grep", ["cwd"] = cwd, ["tool_input"] = input,
        });
    }

    private static bool Redirect(string payload, string? projectDir = Root, bool indexed = true)
        => HookPayloads.ShouldRedirectGrep(payload, projectDir, _ => indexed);

    [Theory]
    [InlineData("FindReferences")]
    [InlineData("image.size")]                 // a bare dot: the literal hit is what was meant
    [InlineData(@"Foo\(bar\)")]                 // escaped punctuation is literal
    [InlineData("could not open file")]
    public void LiteralPatternOverTheWholeProject_IsRedirected(string pattern)
    {
        Assert.True(Redirect(Payload(pattern)));
        Assert.True(Redirect(Payload(pattern, path: Root)));
        Assert.True(Redirect(Payload(pattern, path: Root + @"\")));
        Assert.True(Redirect(Payload(pattern, path: ".")));
        Assert.True(Redirect(Payload(pattern, path: "..", cwd: Root + @"\src")));
    }

    [Theory]
    [InlineData("-i", true)]
    [InlineData("-n", true)]
    [InlineData("head_limit", 20)]
    [InlineData("offset", 5)]
    [InlineData("output_mode", "content")]
    [InlineData("output_mode", "files_with_matches")]
    [InlineData("multiline", false)]
    public void OptionsSearchCodeHas_KeepTheRedirect(string key, object value)
        => Assert.True(Redirect(Payload("Name", extra: new() { [key] = value })));

    [Theory]
    [InlineData("glob", "*.cs")]
    [InlineData("type", "cs")]
    [InlineData("-A", 2)]
    [InlineData("-B", 2)]
    [InlineData("-C", 3)]
    [InlineData("context", 3)]
    [InlineData("output_mode", "count")]
    [InlineData("multiline", true)]
    [InlineData("some_future_option", "x")]
    public void OptionsSearchCodeLacks_PassThroughToGrep(string key, object value)
        => Assert.False(Redirect(Payload("Name", extra: new() { [key] = value })));

    [Theory]
    [InlineData("diff|image.size|object.file")]
    [InlineData("foo.*bar")]
    [InlineData("colou?r")]
    [InlineData("[A-Z]Index")]
    [InlineData("^using ")]
    [InlineData("return;$")]
    [InlineData(@"\bName\b")]
    [InlineData(@"\w+Async")]
    [InlineData(@"\<Index\>")]                  // ripgrep word boundaries
    [InlineData("a{2,}")]
    [InlineData("(get|set)Value")]
    [InlineData(@"trailing\")]
    public void RegexPattern_PassesThroughToGrep(string pattern)
        => Assert.False(Redirect(Payload(pattern)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPattern_PassesThroughToGrep(string pattern)
        => Assert.False(Redirect(Payload(pattern)));

    [Theory]
    [InlineData(@"C:\temp")]
    [InlineData(@"C:\repo-other\src")]
    [InlineData(@"..\elsewhere")]
    [InlineData("~")]
    [InlineData("~/.claude/projects")]
    [InlineData(@"src")]                        // a subfolder: search_code can't scope to it
    [InlineData(@"C:\repo\src\App\bin")]
    [InlineData(@"src\Program.cs")]             // a single file
    public void PathNotTheWholeProject_PassesThroughToGrep(string path)
        => Assert.False(Redirect(Payload("Name", path: path)));

    [Fact]
    public void NoPath_SearchesTheCwd_SoACwdOtherThanTheProjectPassesThrough()
    {
        Assert.False(Redirect(Payload("Name", cwd: Root + @"\src")));
        Assert.False(Redirect(Payload("Name", cwd: @"C:\added-dir")));
    }

    [Fact]
    public void TheProjectDir_NotTheCwd_IsTheIndexedRoot()
    {
        // cwd moved into src: a search of the project root is still the indexed scope...
        Assert.True(Redirect(Payload("Name", path: Root, cwd: Root + @"\src")));
        // ...and with no project dir known, the payload's cwd stands in for it.
        Assert.True(Redirect(Payload("Name"), projectDir: null));
    }

    [Fact]
    public void Tilde_IsTheHomeDirectory_NotAFolderUnderTheCwd()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(Redirect(Payload("Name", path: "~", cwd: Root), projectDir: home));
        Assert.True(Redirect(Payload("Name", path: "~/", cwd: Root), projectDir: home));
    }

    [Fact]
    public void NoIndexYet_PassesThroughToGrep()
        => Assert.False(Redirect(Payload("Name"), indexed: false));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("{\"tool_input\":{\"pattern\":5}}")]
    [InlineData("{\"cwd\":\"C:\\repo\",\"tool_input\":{\"pattern\":\"x\",\"path\":\"C:\\a\u0000b\"}}")]
    public void UnreadablePayload_KeepsTheRedirect_AndNeverThrows(string payload)
        => Assert.True(Redirect(payload));

    [Fact]
    public void AnUnexpectedFailure_KeepsTheRedirect_AndNeverThrows()
        => Assert.True(HookPayloads.ShouldRedirectGrep(Payload("Name"), Root, _ => throw new InvalidOperationException()));

    [Fact]
    public void DenyReason_SaysWhatStillGoesToGrep()
    {
        using var doc = JsonDocument.Parse(HookPayloads.DenySearch());
        var reason = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecisionReason").GetString()!;
        Assert.Contains("regex", reason);
        Assert.Contains("subfolder", reason);
    }
}
