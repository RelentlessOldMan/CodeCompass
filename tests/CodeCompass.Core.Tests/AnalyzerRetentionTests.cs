using System;
using System.IO;
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
    // MayAffect judges a path by its NAME: the files the analyzer reads. A folder, or a path that's gone, can't be judged by
    // name - it may have held .cs files (a renamed or deleted folder) or be an editor's vanished temp file - so the server
    // also asks the index what it actually changed (the folder and atomic-save tests below). Treating every such path as
    // a C# change, as this did before, threw the model away on nearly every save: Visual Studio, JetBrains and Claude
    // Code's own Edit/Write all save through a temp file.
    [Theory]
    [InlineData("README.md", false)]
    [InlineData("appsettings.json", false)]
    [InlineData("src/app.ts", false)]
    [InlineData("A.cs", true)]
    [InlineData("App.CSPROJ", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData(".codecompass.json", true)]                // its ignore/keepDirs decide which folders the analyzer walks
    [InlineData("src/MyApp.Core", false)]                  // a folder: judged by what the index finds under it
    [InlineData("README.md.tmp.16052.1eb1afb6454f", false)] // an editor's temp file, gone by the time the batch lands
    [InlineData(".editorconfig", false)]                   // the analyzer doesn't read it
    public void MayAffect_JudgesAPathByName(string rel, bool expected) =>
        Assert.Equal(expected, RoslynCSharpAnalyzer.MayAffect(new[] { System.IO.Path.Combine(System.IO.Path.GetTempPath(), rel) }));

    [Fact]
    public void WatcherDelete_OfAFolderOfCSharpFiles_DropsAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("Contoso.Lib/a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("b.cs", CodeCompassTools.FindReferences("Ping"));
            System.IO.Directory.Delete(repo.FullPath("Contoso.Lib"), recursive: true);
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("Contoso.Lib") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a deleted folder of .cs files must drop the analyzer");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // What an atomic save delivers: the editor's temp file and backup (gone by the time the batch lands), the folder whose
    // timestamp moved, and the saved file - here Visual Studio saving a .json, plus Claude Code's Write leaving a temp
    // file. None of it touches a C# source, so the ~15 s model stays.
    [Fact]
    public void AnAtomicSaveOfANonCSharpFile_KeepsTheAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.cs", "class A { void M() { } void N() { M(); } }\n");
        repo.Write("src/appsettings.json", "{ }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("M"));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers());

            repo.Write("src/appsettings.json", "{ \"edited\": true }\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[]
            {
                repo.FullPath("src/nbascn0c.1wy~"), repo.FullPath("src"), repo.FullPath("src/appsettings.json~RF381a1f2.TMP"),
                repo.FullPath("src/appsettings.json"), repo.FullPath("README.md.tmp.16052.1eb1afb6454f"),
            }, FullReconcile: false));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "an atomic save of a .json must keep the C# analyzer");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // The index write failing (a full disk, a locked manifest) doesn't undo the edit on disk: the stale model must still go.
    [Fact]
    public void AFailedIndexWrite_ForACSharpEdit_StillDropsTheAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() { } void N() { M(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("M"));
            repo.Write("a.cs", "class A { void M() { } void N() { M(); M(); } }\n");
            var manifest = System.IO.Path.Combine(IndexStore.CacheDirPath(repo.Root), "segments.manifest");
            Assert.True(System.IO.File.Exists(manifest));
            using (new System.IO.FileStream(manifest, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("a.cs") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "the .cs changed on disk even though indexing it failed");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // A model built while a file was locked (an editor, a scanner) lacks that file; the note promises it's picked up after
    // a later edit, so a kept model must not outlive one - a README edit included - once its retry window has passed...
    [Fact]
    public void AnIncompleteAnalyzer_IsNotKeptAcrossAnEdit()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        repo.Write("README.md", "# readme\n");
        var oldWindow = ServerContext.IncompleteModelRetryMs;
        ServerContext.Init(repo.Root);
        try
        {
            ServerContext.IncompleteModelRetryMs = 0;
            CodeCompassTools.Reindex();
            using (new System.IO.FileStream(repo.FullPath("b.cs"), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                Assert.Contains("could not be read when the semantic model was built", CodeCompassTools.FindReferences("Ping"));

            repo.Write("README.md", "# readme, edited\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("README.md") }, FullReconcile: false));
            Assert.DoesNotContain("could not be read when the semantic model was built", CodeCompassTools.FindReferences("Ping"));
        }
        finally
        {
            ServerContext.IncompleteModelRetryMs = oldWindow;
            ServerContext.Init(repo.Root);
        }
    }

    // ...but not rebuilt on EVERY write: with a linked share offline, each rebuild costs a full build plus the share's
    // timeout, and comes back incomplete again. Within the window an unrelated edit keeps it.
    [Fact]
    public void AnIncompleteAnalyzer_IsRetriedAtMostOncePerWindow()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        repo.Write("README.md", "# readme\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            using (new System.IO.FileStream(repo.FullPath("b.cs"), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                Assert.Contains("could not be read when the semantic model was built", CodeCompassTools.FindReferences("Ping"));

            repo.Write("README.md", "# readme, edited\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("README.md") }, FullReconcile: false));
            Assert.True(ServerContext.HasResidentSemanticAnalyzers(), "a model built moments ago is kept, incomplete or not");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // A folder REPLACED by a same-named one within the debounce window arrives as just the folder's path; the walk found the
    // new files but never dropped the old folder's missing ones - from the index, and from the C# model.
    [Fact]
    public void AFolderReplacedByASameNamedOne_DropsTheFilesItNoLongerHas()
    {
        using var repo = new TempRepo();
        using var away = new TempRepo();
        repo.Write("src/a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("src/b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("b.cs", CodeCompassTools.FindReferences("Ping"));
            System.IO.Directory.Move(repo.FullPath("src"), System.IO.Path.Combine(away.Root, "src")); // moved out...
            repo.Write("src/a.cs", "public class A { public void Ping() { } }\n");                     // ...a new one moved in
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("src") }, FullReconcile: false));

            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "b.cs is gone: the model that held it must go");
            Assert.DoesNotContain("b.cs", CodeCompassTools.SearchCode("a.Ping()"));
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // A walk that couldn't list a folder (a share hiccup) left its .cs files out of the model with no word about it: the C#
    // pass counted as complete, so .cs files weren't name-searched either, and references there went missing silently.
    [Fact]
    public void AFolderTheModelsWalkCouldNotList_IsDisclosed_AndNameSearched()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("Far/b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            CodeCompass.Core.Walking.FileWalker.GlobalBeforeReadDirHook = dir =>
            {
                if (dir.EndsWith("Far", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("simulated");
            };
            string r;
            try { r = CodeCompassTools.FindReferences("Ping"); } // builds the model now, with Far/ unlisted
            finally { CodeCompass.Core.Walking.FileWalker.GlobalBeforeReadDirHook = null; }
            Assert.Contains("could not be listed when the semantic model was built", r);
            Assert.Contains("Far/b.cs", r.Replace('\\', '/')); // found by name instead
        }
        finally { ServerContext.Init(repo.Root); }
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
    public void WatcherEdit_ToNonCSharpFile_KeepsAnalyzer_CSharpEdit_UpdatesItInPlace()
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

            var model = ServerContext.ResidentCSharpForTest;
            repo.Write("a.cs", "class A { void M() { } void N() { M(); M(); } }\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("a.cs") }, FullReconcile: false));
            Assert.Same(model, ServerContext.ResidentCSharpForTest); // updated, not thrown away
            Assert.Contains("a.cs:1:40", CodeCompassTools.FindReferences("M")); // the new call is found
            Assert.Equal(1, model!.Builds);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // A new file in a new folder arrives as the folder and the file: the index's C# changes are all files the batch names,
    // so the model takes them in place. A deleted file goes the same way.
    [Fact]
    public void ACSharpFileAddedInANewFolder_AndOneDeleted_UpdateTheModelInPlace()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("b.cs", CodeCompassTools.FindReferences("Ping"));
            var model = ServerContext.ResidentCSharpForTest;

            repo.Write("New/c.cs", "class C { void N(A a) { a.Ping(); } }\n");
            repo.Delete("b.cs");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("New"), repo.FullPath("New/c.cs"), repo.FullPath("b.cs") }, FullReconcile: false));

            Assert.Same(model, ServerContext.ResidentCSharpForTest);
            var r = CodeCompassTools.FindReferences("Ping").Replace('\\', '/');
            Assert.Contains("New/c.cs:1:27", r);
            Assert.DoesNotContain("b.cs", r);
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // A new file the index couldn't read (still held open by its writer) isn't in the index, but a cold build lists it as
    // unreadable - and discloses it. Taking "not in the index" as "gone" would leave it out silently: that's a rebuild.
    [Fact]
    public void ANewCSharpFileTheIndexCantRead_DropsTheAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } void Q() { Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("Ping"));
            Assert.NotNull(ServerContext.ResidentCSharpForTest);
            repo.Write("b.cs", "class B { void N(A a) { a.Ping(); } }\n");
            using (new FileStream(repo.FullPath("b.cs"), FileMode.Open, FileAccess.Read, FileShare.None))
                ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("b.cs") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a C# file the index couldn't read must rebuild the model");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // A binary-looking .cs found by walking a new folder: the index keeps only a binary marker, so nothing reports a change,
    // yet a cold build reads its text. That's a rebuild too.
    [Fact]
    public void ABinaryLookingCSharpFileInANewFolder_DropsTheAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Ping() { } void Q() { Ping(); } }\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("Ping"));
            Assert.NotNull(ServerContext.ResidentCSharpForTest);
            repo.Write("New/c.cs", "class C { void N(A a) { a.Ping(); } }\n\0\0\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("New") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a C# file the index holds no text for must rebuild the model");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // A project file decides the global usings every file binds with: that's a rebuild.
    [Fact]
    public void AProjectFileEdit_DropsTheAnalyzer()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() { } void N() { M(); } }\n");
        repo.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("M"));
            repo.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>\n");
            repo.Write("a.cs", "class A { void M() { } void N() { M(); M(); } }\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("App.csproj"), repo.FullPath("a.cs") }, FullReconcile: false));
            Assert.False(ServerContext.HasResidentSemanticAnalyzers(), "a project file edit must rebuild the model");
        }
        finally { ServerContext.Init(repo.Root); }
    }

    // The commit for an in-place update publishes a new sources token. The model already holds those sources, so it takes
    // the token: a later reload (another process's write that changed no source) must not see "sources moved" and drop it.
    [Fact]
    public void AModelUpdatedInPlace_SurvivesAReloadThatChangedNoSource()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() { } void N() { M(); } }\n");
        repo.Write("README.md", "# readme\n");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("a.cs", CodeCompassTools.FindReferences("M"));
            var model = ServerContext.ResidentCSharpForTest;
            repo.Write("a.cs", "class A { void M() { } void N() { M(); M(); } }\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("a.cs") }, FullReconcile: false));
            Assert.Same(model, ServerContext.ResidentCSharpForTest);

            IndexGeneration.Bump(IndexStore.CacheDirPath(repo.Root), carrySources: true); // another process: index only
            repo.Write("README.md", "# readme, edited\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath("README.md") }, FullReconcile: false));
            Assert.Same(model, ServerContext.ResidentCSharpForTest);
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

    // The per-file #if answers are cached for a model's lifetime; a new model (its sources changed) must work them out again.
    [Fact]
    public void ConditionalAnswers_BelongToOneModel()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void M() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var candidates = new[] { repo.FullPath("a.cs") };
        Assert.Empty(analyzer.ConditionalFiles(candidates));
        repo.Write("a.cs", "class A { void M() {\n#if DEBUG\n M();\n#endif\n } }\n");
        Assert.Empty(analyzer.ConditionalFiles(candidates)); // this model parsed the old text
        analyzer.Dispose();                                   // dropped (as when its sources change): rebuilt on next use
        Assert.Equal(0, analyzer.CachedFileAnswers);          // ...its answers released with it
        Assert.Equal(candidates, analyzer.ConditionalFiles(candidates));
    }

    // Roslyn's file-path map ignores case on every OS. On a case-sensitive folder (Windows per-folder case sensitivity,
    // WSL, Linux) Reg.cs and reg.cs are different files, and each must be judged on its own text.
    [Fact]
    public void ConditionalCheck_TellsCaseTwinsApart()
    {
        using var repo = new TempRepo();
        var src = repo.FullPath("src");
        System.IO.Directory.CreateDirectory(src);
        if (!TestFileSystem.TryMakeCaseSensitive(src)) return; // this box can't: nothing to test
        repo.Write("P.cs", "public class P { public void Ping() { } }\n");
        repo.Write("src/Reg.cs", "class Reg { void A(P p) { p.Ping(); } }\n");
        repo.Write("src/reg.cs", "class reg { void B(P p) {\n#if DEBUG\n p.Ping();\n#endif\n } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        var twins = new[] { repo.FullPath("src/Reg.cs"), repo.FullPath("src/reg.cs") };
        Assert.Equal(new[] { repo.FullPath("src/reg.cs") }, analyzer.ConditionalFiles(twins));
    }

    [Fact]
    public void LinkedRootStartupReconcile_KeepsAnalyzer_OnlyWhenNothingChanged()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE");
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
            Environment.SetEnvironmentVariable("CODECOMPASS_AUTO_RECONCILE", "false"); // the test runs the reconcile itself
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
