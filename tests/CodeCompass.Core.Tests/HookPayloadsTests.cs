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
}
