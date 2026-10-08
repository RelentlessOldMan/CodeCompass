using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// find_references drops every symbol-index position as a "definition" so a name match never lists where a symbol is
// defined. C/C++ symbols became definitions-only at content v4; an index built before that also recorded type USES
// (`struct node *n`) and macro calls (`list_for_each(...) {`) as symbols, so trusting its C/C++ positions silently
// dropped real uses until a full rebuild. Such an index's C/C++ positions are not treated as definitions (the
// definition line may then be listed as a use - over-reporting, never a silent miss); other languages are unaffected.
public class OldIndexDefinitionKeysTests
{
    private static readonly Symbol[] Defs =
    {
        new("node", SymbolKind.Class, "list.h", 3, 8),
        new("node", SymbolKind.Function, "util.c", 10, 5),
        new("node", SymbolKind.Function, "tool.py", 2, 5),
    };

    [Fact]
    public void CurrentIndex_AllPositionsAreDefinitions()
        => Assert.Equal(new[] { "list.h:3:8", "util.c:10:5", "tool.py:2:5" },
                        ReferenceMerge.DefinitionKeys(Defs, r => r, ReferenceMerge.CFamilyDefinitionsOnlySince).ToArray());

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void PreV4Index_CFamilyPositionsAreNotDefinitions(int contentVersion)
        => Assert.Equal(new[] { "tool.py:2:5" }, ReferenceMerge.DefinitionKeys(Defs, r => r, contentVersion).ToArray());

    [Fact]
    public void Mcp_FindReferences_OnAPreV4Index_DoesNotDropCFamilyPositions()
    {
        using var repo = new TempRepo();
        repo.Write("defs.c", "int helper(int x)\n{\n    return x;\n}\n");
        repo.Write("use.c", "int helper(int x);\nint f(void) { return helper(1); }\n");
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            var current = CodeCompass.Mcp.CodeCompassTools.FindReferences("helper");
            Assert.Contains("use.c:2:", current);
            Assert.DoesNotContain("defs.c:1:", current);          // a current index: the definition isn't a use

            // The same index, stamped as built by a v3 indexer: its C/C++ positions can't be trusted as definitions.
            var metaPath = Path.Combine(IndexStore.CacheDirPath(repo.Root), "meta.json");
            var meta = JsonNode.Parse(File.ReadAllText(metaPath))!;
            meta["ContentVersion"] = 3;
            File.WriteAllText(metaPath, meta.ToJsonString());

            var old = CodeCompass.Mcp.CodeCompassTools.FindReferences("helper");
            Assert.Contains("use.c:2:", old);
            Assert.Contains("defs.c:1:", old);                    // listed rather than risk dropping a real use
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void Cli_Refs_OnAPreV4Index_DoesNotDropCFamilyPositions()
    {
        var cli = TestCli.Find();
        using var repo = new TempRepo();
        repo.Write("defs.c", "int helper(int x)\n{\n    return x;\n}\n");
        repo.Write("use.c", "int helper(int x);\nint f(void) { return helper(1); }\n");
        Run(cli, "index", repo.Root);
        Assert.DoesNotContain("defs.c:1:", Run(cli, "refs", repo.Root, "helper"));

        var metaPath = Path.Combine(IndexStore.CacheDirPath(repo.Root), "meta.json");
        var meta = JsonNode.Parse(File.ReadAllText(metaPath))!;
        meta["ContentVersion"] = 3;
        File.WriteAllText(metaPath, meta.ToJsonString());

        var old = Run(cli, "refs", repo.Root, "helper");
        Assert.Contains("use.c:2:", old);
        Assert.Contains("defs.c:1:", old);
    }

    private static string Run(string cli, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(cli)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "CLI did not exit");
        return o.GetAwaiter().GetResult() + "\n" + e.GetAwaiter().GetResult();
    }
}
