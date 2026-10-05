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