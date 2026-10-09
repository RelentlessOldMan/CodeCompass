using System;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Storage;
using CodeCompass.Mcp;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// The resident C# analyzer is the costly part of find_references (EF Core: ~15 s to rebuild, ~0.6 s to query). It must be
// dropped when the C# sources it read may have changed, and kept otherwise: before this, any watched edit (a README,
// a .json) and every linked-root startup reconcile threw it away, so the next C# query paid the full rebuild.
public class AnalyzerRetentionTests
{
    // Only an existing file of a type the analyzer doesn't read is safe to ignore. A directory - dotted names like
    // MyApp.Core included, since a folder rename is one event with no per-file events - or a path that's gone (a
    // deleted or renamed-away folder) may have held .cs files.
    [Theory]
    [InlineData("README.md", false)]
    [InlineData("appsettings.json", false)]
    [InlineData("src/app.ts", false)]
    [InlineData("A.cs", true)]
    [InlineData("App.CSPROJ", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("src/Folder/", true)]         // an existing directory
    [InlineData("src/MyApp.Core/", true)]     // an existing directory with a dot in its name
    [InlineData("Gone.Lib", true)]            // no longer exists: a deleted or renamed-away folder
    [InlineData(".editorconfig", true)]       // dotfiles: config, so assume it matters
    public void AffectsAnalyzer_ByPath(string rel, bool expected)
    {
        using var repo = new TempRepo();
        foreach (var f in new[] { "README.md", "appsettings.json", "src/app.ts", "A.cs", "App.CSPROJ", "Directory.Build.props",
                                  "src/Folder/x.cs", "src/MyApp.Core/y.cs", ".editorconfig" })
            repo.Write(f, "x");
        Assert.Equal(expected, RoslynCSharpAnalyzer.MayAffect(new[] { repo.FullPath(rel.TrimEnd('/')) }));
    }

    [Fact]
    public void WatcherRename_OfADottedFolder_DropsAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("MyApp.Core/a.cs", "class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("b.cs", CodeCompassTools.FindReferences("Ping"));
            System.IO.Directory.Move(repo.FullPath("MyApp.Core"), repo.FullPath("MyApp.Domain"));
            // Windows reports a folder rename as one event for the folder itself (old and new path), nothing per file.
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("MyApp.Core"), repo.FullPath("MyApp.Domain") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a renamed folder of .cs files must drop the analyzer");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void WatcherEdit_ToNonCSharpFile_KeepsAnalyzer_CSharpEdit_DropsIt()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() { } void N() { M(); } }\n");
        repo.Write("README.md", "# readme\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("M"));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers());

            repo.Write("README.md", "# readme, edited\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("README.md") }, FullReconcile: false));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "a README edit must keep the C# analyzer");

            repo.Write("a.cs", "class A { void M() { } void N() { M(); M(); } }\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("a.cs") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a .cs edit must drop the stale analyzer");
            Assert.Contains("a.cs:1:40", CodeCompassTools.FindReferences("M")); // the new call is found
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // The #if check used to re-read every .cs candidate from disk on every query. It now reads the text the analyzer
    // parsed (the text Roslyn couldn't see into), so a candidate that's locked after the build is still checked - and
    // nothing is read from disk for it.
    [Fact]
    public void ConditionalCheck_UsesTheAnalyzersText_NotADiskReread()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) {\n#if DEBUG\n a.Ping();\n#endif\n a.Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("conditional compilation", CodeCompassTools.FindReferences("Ping")); // builds the analyzer
            using (new System.IO.FileStream(repo.FullPath("b.cs"), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
            {
                var r = CodeCompassTools.FindReferences("Ping");
                Assert.Contains("conditional compilation", r);
                Assert.Contains("b.cs", r);
            }
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void LinkedRootStartupReconcile_KeepsAnalyzer_OnlyWhenNothingChanged()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
        Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false"); // the test runs the reconcile itself
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "class P { void Use() { new Lib().Go(); } }\n");
        lib.Write("lib.cs", "public class Lib { public void Go() { } }\n");
        var (t, s, _) = CodeCompass.Core.Indexing.RepositoryIndexer.Build(lib.Root);
        t.Dispose(); s.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("p.cs", CodeCompassTools.FindReferences("Go"));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers());

            ServerContext.RunLinkedStartupReconcileNow(lib.Root); // nothing changed in the linked root
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "an unchanged linked reconcile must keep the C# analyzer");

            lib.Write("more.cs", "class More { void X() { new Lib().Go(); } }\n");
            ServerContext.RunLinkedStartupReconcileNow(lib.Root);
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a linked reconcile that found changes must drop it");
            Assert.Contains("more.cs", CodeCompassTools.FindReferences("Go"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", old);
            ServerContext.Init(project.Root);
        }
    }
}
