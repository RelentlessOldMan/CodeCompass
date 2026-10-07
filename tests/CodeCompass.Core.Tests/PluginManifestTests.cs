using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace CodeCompass.Core.Tests;

// The plugin installs into Claude Code AND Codex from these manifests; each of the faults below broke an install in the field
// and none showed up in a build. Read as raw bytes and parsed strictly, the way an installer may.
public class PluginManifestTests
{
    private static readonly string[] Manifests =
    {
        "plugin/.claude-plugin/plugin.json", "plugin/.claude-plugin/marketplace.json", "plugin/.mcp.json", "plugin/hooks/hooks.json",
        "plugin/plugin.json", "plugin/mcp.json", "plugin/.agents/plugins/marketplace.json",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "plugin", "plugin.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("can't find the repo root (plugin/plugin.json)");
    }

    private static JsonDocument Strict(string rel)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), rel));
        // A UTF-8 BOM made an installer reject the manifest; ReadAllText would hide it, so check the bytes.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{rel} starts with a UTF-8 BOM");
        // A duplicated "hooks" key was silently last-wins in a lenient parser and dropped the first block.
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
    }

    [Fact]
    public void EveryManifest_HasNoBom_AndParsesStrictly()
    {
        foreach (var rel in Manifests) Strict(rel).Dispose();
    }

    // Each host expands only its own root variable; a hard-coded user path (one worked on the author's box only) or the
    // other host's variable leaves the server unlaunchable.
    [Theory]
    [InlineData("plugin/.mcp.json", "${CLAUDE_PLUGIN_ROOT}")]
    [InlineData("plugin/mcp.json", "${CODEX_PLUGIN_ROOT}")]
    public void McpConfig_LaunchesTheBundledServer_ViaItsHostsRoot(string rel, string rootVar)
    {
        using var doc = Strict(rel);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("codecompass");
        var command = server.GetProperty("command").GetString()!;
        Assert.Equal(rootVar + "/bin/CodeCompass.Mcp.exe", command);
        Assert.DoesNotContain(":\\", doc.RootElement.GetRawText());
        Assert.DoesNotContain("Users", doc.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // build-plugin stamps both manifests from the build; a host that sees a different version than the other host (or than
    // the binary) reports a mismatch on install.
    [Fact]
    public void ClaudeAndCodexManifests_CarryTheSameNameAndVersion()
    {
        using var claude = Strict("plugin/.claude-plugin/plugin.json");
        using var codex = Strict("plugin/plugin.json");
        Assert.Equal(claude.RootElement.GetProperty("name").GetString(), codex.RootElement.GetProperty("name").GetString());
        Assert.Equal(claude.RootElement.GetProperty("version").GetString(), codex.RootElement.GetProperty("version").GetString());
    }
}
