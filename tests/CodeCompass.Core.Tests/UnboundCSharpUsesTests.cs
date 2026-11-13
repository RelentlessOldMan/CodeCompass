using Xunit;

namespace CodeCompass.Core.Tests;

// The semantic model compiles against the framework only: a type from a NuGet package, from source-generator output (obj/
// isn't walked) or declared under an inactive #if is an error type, so a member reached through it binds to nothing and the
// semantic pass skipped it - while the answer read as complete. A file where the name is used but binds to nothing is
// backfilled by name, and the answer says so.
// (The package here is made up: the model references the host's trusted assemblies, and a test host carries Newtonsoft.)
public class UnboundCSharpUsesTests
{
    private static TempRepo Repo(bool withPackageUse)
    {
        var repo = new TempRepo();
        repo.Write("Widget.cs", "namespace N;\npublic class Widget { public void Spin() { } }\n");
        repo.Write("Plain.cs", "class Plain\n{\n    void M(N.Widget w) { w.Spin(); }\n}\n");
        if (withPackageUse)
            repo.Write("Json.cs", """
                using Acme.Serialization;
                class J
                {
                    void M(string s)
                    {
                        var w = AcmeConvert.Read<N.Widget>(s);
                        w.Spin();
                        AcmeConvert.Read<N.Widget>(s).Spin();
                    }
                }
                """);
        return repo;
    }

    private static string Refs(TempRepo repo, string name)
    {
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            return CodeCompass.Mcp.CodeCompassTools.FindReferences(name);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void UsesThroughAPackageType_AreListedByName_AndSaidSo()
    {
        using var repo = Repo(withPackageUse: true);
        var r = Refs(repo, "Spin");
        Assert.Contains("Plain.cs:3:", r);  // resolved semantically
        Assert.Contains("Json.cs:7:", r);   // w.Spin(), w typed by the unresolved AcmeConvert
        Assert.Contains("Json.cs:8:", r);
        Assert.Contains("in Json.cs \"Spin\" is used through types the semantic model can't resolve", r); // names the file
        Assert.DoesNotContain("Plain.cs \"Spin\"", r); // ...and only it: Plain.cs resolved
    }

    [Fact]
    public void APackageTypeItself_IsListedByName_AndSaidSo()
    {
        using var repo = Repo(withPackageUse: true);
        var r = Refs(repo, "AcmeConvert");
        Assert.Contains("Json.cs:6:", r);
        Assert.Contains("Json.cs:8:", r);
        Assert.Contains("can't resolve", r);
    }

    [Fact]
    public void Cli_UsesThroughAPackageType_AreListedByName_AndSaidSo()
    {
        var cli = TestCli.Find();
        using var repo = Repo(withPackageUse: true);
        Run(cli, "index", repo.Root);
        var r = Run(cli, "refs", repo.Root, "Spin");
        Assert.Contains("Plain.cs:3:", r);
        Assert.Contains("Json.cs:7:", r);
        Assert.Contains("Json.cs:8:", r);
        Assert.Contains("in Json.cs \"Spin\" is used through types the semantic model can't resolve", r);
    }

    [Fact]
    public void EveryUseResolved_NoNote()
    {
        using var repo = Repo(withPackageUse: false);
        var r = Refs(repo, "Spin");
        Assert.Contains("Plain.cs:3:", r);
        Assert.DoesNotContain("can't resolve", r);
    }

    // A package's namespace in a using directive, and a namespace declaration, name namespaces - not uses of the symbol.
    [Fact]
    public void ANamespaceNamedLikeTheSymbol_IsNotAnUnboundUse()
    {
        using var repo = new TempRepo();
        repo.Write("Widget.cs", "namespace N;\npublic class Widget { public void Spin() { } }\n");
        repo.Write("Ns.cs", "using Vendor.Spin.Extras;\nnamespace App.Spin\n{\n    class C { void M(N.Widget w) { w.Spin(); } }\n}\n");
        var r = Refs(repo, "Spin");
        Assert.Contains("Ns.cs:4:", r);
        Assert.DoesNotContain("can't resolve", r);
    }

    // A named argument to a package's method names its parameter: not a use. A property pattern's name is one.
    [Fact]
    public void NamedArgument_IsNotAUse_ButAPropertyPatternIs()
    {
        using var repo = new TempRepo();
        repo.Write("Widget.cs", "namespace N;\npublic class Widget { public int Size; }\n");
        repo.Write("Arg.cs", "class A { void M() { Acme.Pack.Make(Size: 1); } }\n");
        var r = Refs(repo, "Size");
        Assert.DoesNotContain("can't resolve", r);

        repo.Write("Pat.cs", "class P { bool M(string s) => Acme.Pack.Load(s) is { Size: 1 }; }\n");
        r = Refs(repo, "Size");
        Assert.Contains("in Pat.cs \"Size\" is used through types the semantic model can't resolve", r);
        Assert.DoesNotContain("Arg.cs \"Size\"", r);
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
