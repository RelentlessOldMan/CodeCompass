using System.Linq;
using CodeCompass.Core.Symbols;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// C# records were invisible: the tree-sitter query had no record rule (find_definition / search_symbols missed every
// record, nested or not), and Roslyn's by-name lookup returned a positional record's primary-constructor PARAMETER
// instead of its synthesized property, so find_references on e.g. FileState.ContentHash resolved to "0" - and with
// the C# pass "complete", the lexical net skipped .cs files too.
public class CSharpRecordTests
{
    [Fact]
    public void Extract_FindsRecordsAndRecordStructs_IncludingNested()
    {
        using var ex = new TreeSitterSymbolExtractor();
        var syms = ex.Extract("a.cs", """
            namespace N;
            public sealed record TopRecord(int A);
            public record class TopRecordClass { }
            public sealed class Outer
            {
                public readonly record struct NestedStruct(int X, string Y);
            }
            """);

        foreach (var name in new[] { "TopRecord", "TopRecordClass", "NestedStruct" })
        {
            var s = Assert.Single(syms, s => s.Name == name);
            Assert.Equal(SymbolKind.Record, s.Kind);
        }
    }

    [Fact]
    public void Extract_FindsPositionalRecordProperties()
    {
        using var ex = new TreeSitterSymbolExtractor();
        var syms = ex.Extract("a.cs", "namespace N;\npublic readonly record struct FileState(long Size, string ContentHash);\n");

        var p = Assert.Single(syms, s => s.Name == "ContentHash");
        Assert.Equal(SymbolKind.Property, p.Kind);
        Assert.Equal(2, p.Line);
    }

    [Fact]
    public void FindReferences_ResolvesPositionalRecordProperty()
    {
        using var repo = new TempRepo();
        repo.Write("FileState.cs", """
            namespace App;
            public readonly record struct FileState(long Size, string ContentHash)
            {
                public bool IsEmpty => ContentHash == "";
            }
            """);
        repo.Write("Use.cs", """
            namespace App;
            using System.Collections.Generic;
            public static class Use
            {
                public static string Hash(FileState f) => f.ContentHash;
                public static FileState Make() => new FileState(1, ContentHash: "x");
                public static void All(IReadOnlyDictionary<string, FileState> d)
                {
                    foreach (var (path, state) in d) System.Console.WriteLine(state.ContentHash);
                }
            }
            """);

        var refs = new RoslynCSharpAnalyzer(repo.Root).FindReferences("ContentHash");

        // Every property use counts: a plain access, inside the record's own body, and through a deconstructed generic.
        // (The named argument binds to the ctor parameter - also a real use of the positional member, so it may be
        // reported too.)
        Assert.Contains(refs, r => r.RelativePath == "Use.cs" && r.LineText.Contains("f.ContentHash"));
        Assert.Contains(refs, r => r.RelativePath == "FileState.cs" && r.LineText.Contains("IsEmpty"));
        Assert.Contains(refs, r => r.RelativePath == "Use.cs" && r.LineText.Contains("state.ContentHash"));
    }

    // SDK-style projects with <ImplicitUsings> get System.Collections.Generic/System.Linq/... as invisible global usings.
    // The analyzer's ad-hoc compilation didn't, so `List<Widget>` was an error type and every member access through it
    // silently dropped out of find_references (and the "complete" C# pass suppressed the lexical net) - on most modern
    // .NET repos. Also honors explicit <Using Include> items.
    [Theory]
    [InlineData("<ImplicitUsings>enable</ImplicitUsings>", "")]
    [InlineData("", "<ItemGroup><Using Include=\"System.Collections.Generic\" /></ItemGroup>")]
    public void FindReferences_HonorsProjectImplicitAndGlobalUsings(string property, string items)
    {
        using var repo = new TempRepo();
        repo.Write("App/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework>{property}</PropertyGroup>
              {items}
            </Project>
            """);
        repo.Write("App/Widget.cs", """
            namespace App;
            public sealed class Widget { public int Spin() => 1; }
            """);
        repo.Write("App/Use.cs", """
            namespace App;
            public static class Use
            {
                public static void All(List<Widget> ws) { foreach (var w in ws) w.Spin(); }
            }
            """);

        var refs = new RoslynCSharpAnalyzer(repo.Root).FindReferences("Spin");

        Assert.Contains(refs, r => r.RelativePath == "App/Use.cs" && r.LineText.Contains("w.Spin()"));
    }
}
