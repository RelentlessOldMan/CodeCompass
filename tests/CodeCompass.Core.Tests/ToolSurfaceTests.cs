using System.Linq;
using System.Reflection;
using System.Text.Json;
using CodeCompass.Mcp;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace CodeCompass.Core.Tests;

// The MCP tool definitions are paid for in tokens by EVERY agent session, so their shape is a product contract.
public class ToolSurfaceTests
{
    private readonly ITestOutputHelper _out;
    public ToolSurfaceTests(ITestOutputHelper output) => _out = output;

    private static (string Name, string Json)[] Tools() =>
        typeof(CodeCompassTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m =>
            {
                var tool = McpServerTool.Create(m, target: null);
                return (tool.ProtocolTool.Name, JsonSerializer.Serialize(tool.ProtocolTool));
            })
            .ToArray();

    // Review P1-7: tools take the per-request CancellationToken so an abandoned call stops its clang/Roslyn work. The
    // SDK must bind it invisibly - if it leaked into the input schema, every session would pay for a useless param.
    [Fact]
    public void CancellationToken_IsNotPartOfAnyToolSchema()
    {
        foreach (var (name, json) in Tools())
            Assert.DoesNotContain("cancellationToken", json, System.StringComparison.OrdinalIgnoreCase);
    }

    // Token-minimalism guard (a hard project rule): the whole tool surface - names, descriptions, parameter schemas -
    // must stay small. Fails loudly if a change bloats it, so growth is a deliberate decision, not drift.
    [Fact]
    public void ToolSurface_StaysWithinItsTokenBudget()
    {
        var tools = Tools();
        Assert.Equal(7, tools.Length);
        int total = tools.Sum(t => t.Json.Length);
        foreach (var (name, json) in tools) _out.WriteLine($"{name}: {json.Length} chars");
        _out.WriteLine($"total: {total} chars (~{total / 4} tokens)");
        Assert.True(total <= 6500, $"MCP tool surface is {total} chars - over budget; trim descriptions before adding more.");
    }
}
