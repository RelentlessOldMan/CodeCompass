using System.IO;
using System.Text.Json;
using Xunit;

namespace CodeCompass.Core.Tests;

/// <summary>Both executables must ship with Dynamic PGO off. With it on, the one-time Roslyn build on a session's first C#
/// find_references ran ~4.5 s slower on EF Core (24 s vs 19.5 s); warm calls differ by ~10%. The setting was dropped once
/// as dead when clang went, so this guards the built runtimeconfig.json, the file the runtime actually reads.</summary>
public class RuntimeConfigTests
{
    [Theory]
    [InlineData("CodeCompass.Cli")]
    [InlineData("CodeCompass.Mcp")]
    public void Executable_DisablesTieredPgo(string project)
    {
        var exe = project == "CodeCompass.Mcp" ? TestCli.FindMcp() : TestCli.Find();
        var config = Path.Combine(Path.GetDirectoryName(exe)!, project + ".runtimeconfig.json");
        Assert.True(File.Exists(config), config + " is missing");

        using var doc = JsonDocument.Parse(File.ReadAllText(config));
        var props = doc.RootElement.GetProperty("runtimeOptions");
        Assert.True(props.TryGetProperty("configProperties", out var cp)
                    && cp.TryGetProperty("System.Runtime.TieredPGO", out var pgo)
                    && pgo.ValueKind == JsonValueKind.False,
            $"{config} must set System.Runtime.TieredPGO to false (<TieredPGO>false</TieredPGO> in {project}.csproj)");
    }
}
