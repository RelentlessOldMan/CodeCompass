using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Config;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Walking;
using CodeCompass.Mcp;
using Xunit;

namespace CodeCompass.Core.Tests;

// A repo's own .gitignore and .ignore files: what git leaves out (build output, logs, generated trees under names the
// defaults don't know) is left out of the index too - on by default, `useIgnoreFiles: false` (or
// CODECOMPASS_USE_IGNORE_FILES=0) turns it off. Applied wherever the walk's rules are: the walk, the watcher's targeted
// updates, and the startup prune of an index built before (or without) them.
public class IgnoreFileTests
{
    // ---- the matcher (gitignore syntax) ----

    [Theory]
    [InlineData("*.log", "a.log", false, true)]
    [InlineData("*.log", "src/deep/a.log", false, true)]          // no slash: matches a name at any depth
    [InlineData("*.log", "a.logs", false, false)]
    [InlineData("/top.txt", "top.txt", false, true)]               // leading slash: anchored to the file's folder
    [InlineData("/top.txt", "sub/top.txt", false, false)]
    [InlineData("doc/*.md", "doc/a.md", false, true)]              // a middle slash anchors too
    [InlineData("doc/*.md", "x/doc/a.md", false, false)]
    [InlineData("doc/*.md", "doc/sub/a.md", false, false)]         // * stops at a slash
    [InlineData("gen/", "gen", true, true)]                        // trailing slash: folders only
    [InlineData("gen/", "gen", false, false)]
    [InlineData("**/cache", "cache", true, true)]                  // leading **/: any depth, including none
    [InlineData("**/cache", "a/b/cache", true, true)]
    [InlineData("a/**/z", "a/z", false, true)]                     // /**/: zero or more folders
    [InlineData("a/**/z", "a/b/c/z", false, true)]
    [InlineData("a/**", "a/b/c", false, true)]                     // trailing /**: everything inside
    [InlineData("a/**", "a", true, false)]
    [InlineData("fo?.txt", "foo.txt", false, true)]
    [InlineData("fo?.txt", "fo/.txt", false, false)]
    [InlineData("[ab]c", "bc", false, true)]
    [InlineData("[!ab]c", "bc", false, false)]
    [InlineData("[a-c]x", "bx", false, true)]
    [InlineData("\\#lit", "#lit", false, true)]                     // an escaped # is a name, not a comment
    [InlineData("\\!lit", "!lit", false, true)]
    [InlineData("trail\\ ", "trail ", false, true)]                // an escaped trailing space is kept
    [InlineData("trail   ", "trail", false, true)]                 // unescaped trailing spaces are not
    [InlineData("# comment", "# comment", false, false)]
    [InlineData("*.[[:digit:]]", "core.1", false, true)]          // POSIX classes, as git's wildmatch has them
    [InlineData("*.[[:digit:]]", "core.a", false, false)]
    [InlineData("[[:alpha:]_]x", "_x", false, true)]
    [InlineData("[![:space:]]x", " x", false, false)]
    [InlineData("[[:bogus:]]", "b", false, false)]               // an unknown class matches nothing
    public void Pattern_MatchesAsGitDoes(string pattern, string path, bool isDir, bool ignored)
    {
        var f = IgnoreFile.Parse("", new[] { pattern });
        Assert.Equal(ignored, f.Match(path, isDir) == true);
    }

    [Fact]
    public void LastMatchingPattern_Wins_AndNegationReincludes()
    {
        var f = IgnoreFile.Parse("", new[] { "*.log\n!keep.log\n" });
        Assert.True(f.Match("a.log", false));
        Assert.False(f.Match("keep.log", false));                  // re-included: a definite "not ignored"
        Assert.Null(f.Match("a.cs", false));                        // no pattern speaks
        var g = IgnoreFile.Parse("", new[] { "!keep.log\n*.log\n" });
        Assert.True(g.Match("keep.log", false));                    // the later pattern wins
    }

    [Fact]
    public void Patterns_AreRelativeToTheirOwnFolder()
    {
        var f = IgnoreFile.Parse("src/lib", new[] { "/gen\n" });
        Assert.True(f.Match(f.RelFromBase("src/lib/gen"), true));
        Assert.Null(f.Match(f.RelFromBase("src/lib/x/gen"), true));
    }

    [Fact]
    public void CrLfFiles_Parse()
    {
        var f = IgnoreFile.Parse("", new[] { "*.log\r\nout/\r\n" });
        Assert.True(f.Match("a.log", false));
        Assert.True(f.Match("out", true));
    }

    // ---- the walk ----

    private static TempRepo Sample()
    {
        var repo = new TempRepo();
        repo.Write(".gitignore", "*.log\nartifacts/\n/rootonly.txt\n!important.log\n");
        repo.Write("a.cs", "class A {}");
        repo.Write("run.log", "noise");
        repo.Write("important.log", "kept by negation");
        repo.Write("rootonly.txt", "ignored at the top only");
        repo.Write("src/rootonly.txt", "kept: the pattern is anchored to the root");
        repo.Write("src/deep/trace.log", "noise at depth");
        repo.Write("artifacts/out.cs", "class Out {}");
        repo.Write("artifacts/sub/more.cs", "class More {}");
        repo.Write("lib/.ignore", "secret.cs\n");
        repo.Write("lib/secret.cs", "class Secret {}");
        repo.Write("lib/open.cs", "class Open {}");
        repo.Write("other/secret.cs", "class OtherSecret {}");    // lib's .ignore doesn't reach here
        return repo;
    }

    private static HashSet<string> Walked(string root, IgnoreRules rules, int threads) =>
        new FileWalker(rules, threads).Walk(root).Select(f => f.RelativePath).ToHashSet();

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Walk_LeavesOutWhatTheIgnoreFilesIgnore(int threads)
    {
        using var repo = Sample();
        var got = Walked(repo.Root, new IgnoreRules(new RepoConfig()), threads);
        Assert.Equal(new[] { ".gitignore", "a.cs", "important.log", "lib/.ignore", "lib/open.cs", "other/secret.cs", "src/rootonly.txt" },
                     got.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Walk_OptedOut_IndexesEverything(int threads)
    {
        using var repo = Sample();
        var got = Walked(repo.Root, new IgnoreRules(new RepoConfig { UseIgnoreFiles = false }), threads);
        Assert.Contains("run.log", got);
        Assert.Contains("artifacts/sub/more.cs", got);
        Assert.Contains("lib/secret.cs", got);
    }

    [Fact]
    public void Walk_OptOut_ByEnvironment()
    {
        using var repo = Sample();
        Environment.SetEnvironmentVariable("CODECOMPASS_USE_IGNORE_FILES", "0");
        try { Assert.Contains("run.log", Walked(repo.Root, new IgnoreRules(new RepoConfig()), 1)); }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_USE_IGNORE_FILES", null); }
    }

    [Fact]
    public void Walk_FolderIgnoredAbove_CantBeReincludedBelow()
    {
        using var repo = new TempRepo();
        repo.Write(".gitignore", "gen/\n!gen/keep.cs\n");
        repo.Write("gen/keep.cs", "class Keep {}");
        repo.Write("gen/.gitignore", "!*\n");
        repo.Write("a.cs", "class A {}");
        Assert.DoesNotContain("gen/keep.cs", Walked(repo.Root, new IgnoreRules(new RepoConfig()), 1)); // as git: the folder is never entered
    }

    [Fact]
    public void Walk_DeeperIgnoreFile_OverridesShallower_AndDotIgnoreOverridesGitignore()
    {
        using var repo = new TempRepo();
        repo.Write(".gitignore", "*.gen.cs\n");
        repo.Write("a.gen.cs", "class A {}");
        repo.Write("keep/.gitignore", "!*.gen.cs\n");
        repo.Write("keep/b.gen.cs", "class B {}");
        repo.Write("both/.gitignore", "!c.gen.cs\n");
        repo.Write("both/.ignore", "c.gen.cs\n");                   // .ignore is read after .gitignore: it wins
        repo.Write("both/c.gen.cs", "class C {}");
        var got = Walked(repo.Root, new IgnoreRules(new RepoConfig()), 1);
        Assert.DoesNotContain("a.gen.cs", got);
        Assert.Contains("keep/b.gen.cs", got);
        Assert.DoesNotContain("both/c.gen.cs", got);
    }

    [Fact]
    public void Walk_KeepDirs_WinOverIgnoreFiles()
    {
        using var repo = new TempRepo();
        repo.Write(".gitignore", "vendor/\n");
        repo.Write("vendor/lib.cs", "class Lib {}");
        var got = Walked(repo.Root, new IgnoreRules(new RepoConfig { KeepDirs = new[] { "vendor" } }), 1);
        Assert.Contains("vendor/lib.cs", got);
    }

    [Fact]
    public void Walk_IgnoreFilesThemselves_StayIndexed_EvenIfAPatternNamesThem()
    {
        using var repo = new TempRepo();
        repo.Write(".gitignore", ".*\n");                            // a dotfile pattern catches .gitignore too
        repo.Write("a.cs", "class A {}");
        var got = Walked(repo.Root, new IgnoreRules(new RepoConfig()), 1);
        Assert.Contains(".gitignore", got);                         // the index must hold every ignore file it applies
    }

    [Fact]
    public void Walk_OfASubfolder_AppliesTheIgnoreFilesAboveIt()
    {
        using var repo = Sample();
        var set = IgnoreFileSet.ForSnapshot(repo.Root, new IgnoreRules(new RepoConfig()), new[] { ".gitignore", "lib/.ignore" });
        var got = new FileWalker(new IgnoreRules(new RepoConfig()), 1)
            .Walk(repo.FullPath("src"), set.ScopeOf("")!, "src")
            .Select(f => f.RelativePath).ToHashSet();
        Assert.Equal(new[] { "rootonly.txt" }, got);                // src/deep/trace.log is left out by the root's *.log
    }

    // ---- judging one path without a walk (the watcher's targeted updates, the startup prune) ----

    [Fact]
    public void Set_JudgesAPath_AsTheWalkWould()
    {
        using var repo = Sample();
        var set = IgnoreFileSet.ForSnapshot(repo.Root, new IgnoreRules(new RepoConfig()), new[] { ".gitignore", "lib/.ignore" });
        Assert.True(set.IsIgnored("run.log", false));
        Assert.False(set.IsIgnored("important.log", false));
        Assert.True(set.IsIgnored("artifacts/sub/more.cs", false));    // under an ignored folder
        Assert.True(set.IsIgnored("artifacts", true));
        Assert.True(set.IsIgnored("lib/secret.cs", false));
        Assert.False(set.IsIgnored("other/secret.cs", false));
        Assert.False(set.IsIgnored("src/rootonly.txt", false));
        Assert.False(set.IsIgnored(".gitignore", false));
    }

    // ---- the index ----

    [Fact]
    public void Build_LeavesIgnoredFilesOut()
    {
        using var repo = Sample();
        var b = RepositoryIndexer.Build(repo.Root);
        try
        {
            var paths = b.Text.AllPaths();
            Assert.Contains("a.cs", paths);
            Assert.DoesNotContain("run.log", paths);
            Assert.DoesNotContain("artifacts/out.cs", paths);
            Assert.Empty(b.Symbols.FindByName("Secret"));
        }
        finally { b.Text.Dispose(); b.Symbols.Dispose(); }
    }

    [Fact]
    public void Build_OptedOutInConfig_IndexesThem()
    {
        using var repo = Sample();
        repo.Write(".codecompass.json", "{ \"useIgnoreFiles\": false }");
        var b = RepositoryIndexer.Build(repo.Root);
        try { Assert.Contains("artifacts/out.cs", b.Text.AllPaths()); }
        finally { b.Text.Dispose(); b.Symbols.Dispose(); }
    }

    private static ChangeCounts Apply(TempRepo repo, params string[] rels)
    {
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        using var snapshot = RepositoryIndexer.LoadSnapshot(repo.Root);
        try
        {
            var c = RepositoryIndexer.ApplyChanges(text, symbols, snapshot, repo.Root, rels.Select(repo.FullPath));
            RepositoryIndexer.Persist(repo.Root, text, symbols, snapshot);
            return c;
        }
        finally { text.Dispose(); symbols.Dispose(); }
    }

    private static IReadOnlyCollection<string> IndexedPaths(TempRepo repo)
    {
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        try { return text.AllPaths().ToList(); }
        finally { text.Dispose(); symbols.Dispose(); }
    }

    [Fact]
    public void ApplyChanges_NewIgnoredFile_IsNotAdded()
    {
        using var repo = Sample();
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        repo.Write("src/new.log", "noise");
        repo.Write("artifacts/fresh.cs", "class Fresh {}");
        repo.Write("src/new.cs", "class New {}");
        var c = Apply(repo, "src/new.log", "artifacts/fresh.cs", "src/new.cs");
        Assert.Equal(1, c.Added);
        var paths = IndexedPaths(repo);
        Assert.Contains("src/new.cs", paths);
        Assert.DoesNotContain("src/new.log", paths);
        Assert.DoesNotContain("artifacts/fresh.cs", paths);
        Assert.False(c.IgnoreFilesChanged);
    }

    [Fact]
    public void ApplyChanges_NewFolder_IsWalkedWithTheIgnoreFilesAboveIt()
    {
        using var repo = Sample();
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        repo.Write("pkg/x.cs", "class X {}");
        repo.Write("pkg/x.log", "noise");
        repo.Write("pkg/artifacts/y.cs", "class Y {}");               // artifacts/ has no slash before it: any depth
        repo.Write("artifacts/new/z.cs", "class Z {}");               // a folder made inside an ignored one
        Apply(repo, "pkg", "artifacts/new");
        var paths = IndexedPaths(repo);
        Assert.Contains("pkg/x.cs", paths);
        Assert.DoesNotContain("pkg/x.log", paths);
        Assert.DoesNotContain("pkg/artifacts/y.cs", paths);
        Assert.DoesNotContain("artifacts/new/z.cs", paths);
    }

    [Fact]
    public void ApplyChanges_IgnoreFileEdited_DropsWhatItNowIgnores_AndSaysSo()
    {
        using var repo = Sample();
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        Assert.Contains("lib/open.cs", IndexedPaths(repo));
        repo.Write("lib/.ignore", "secret.cs\nopen.cs\n");
        var c = Apply(repo, "lib/.ignore");
        Assert.True(c.IgnoreFilesChanged);                          // newly un-ignored files need a walk: the caller's call
        Assert.Equal(1, c.Removed);
        var paths = IndexedPaths(repo);
        Assert.DoesNotContain("lib/open.cs", paths);
        Assert.Contains("lib/.ignore", paths);
        Assert.Contains("other/secret.cs", paths);
    }

    [Fact]
    public void ApplyChanges_RootIgnoreFileDeleted_SaysSo()
    {
        using var repo = Sample();
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        repo.Delete(".gitignore");
        var c = Apply(repo, ".gitignore");
        Assert.True(c.IgnoreFilesChanged);
        Assert.DoesNotContain(".gitignore", IndexedPaths(repo));
    }

    [Fact]
    public void Update_PicksUpWhatAnIgnoreFileNoLongerIgnores_AndDropsWhatItNow_Does()
    {
        using var repo = Sample();
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        repo.Write(".gitignore", "*.cs\n");
        File.SetLastWriteTimeUtc(repo.FullPath(".gitignore"), DateTime.UtcNow.AddSeconds(5));
        var u = RepositoryIndexer.Update(repo.Root);
        try
        {
            var paths = u.Text.AllPaths();
            Assert.Contains("run.log", paths);                      // no longer ignored: added
            Assert.Contains("rootonly.txt", paths);
            Assert.DoesNotContain("a.cs", paths);                   // newly ignored: dropped
            Assert.DoesNotContain("artifacts/out.cs", paths);
        }
        finally { u.Text.Dispose(); u.Symbols.Dispose(); }
    }

    [Fact]
    public void PruneIgnored_DropsWhatTheIgnoreFilesIgnore_FromAnIndexBuiltWithout()
    {
        using var repo = Sample();
        repo.Write(".codecompass.json", "{ \"useIgnoreFiles\": false }");
        RepositoryIndexer.Build(repo.Root).Text.Dispose();          // an index from before ignore files were read
        Assert.Contains("artifacts/out.cs", IndexedPaths(repo));
        repo.Write(".codecompass.json", "{ }");

        var u = RepositoryIndexer.PruneIgnored(repo.Root);
        try
        {
            var paths = u.Text.AllPaths();
            Assert.DoesNotContain("artifacts/out.cs", paths);
            Assert.DoesNotContain("run.log", paths);
            Assert.DoesNotContain("lib/secret.cs", paths);
            Assert.Contains("a.cs", paths);
            Assert.Contains("important.log", paths);
            // run.log, src/deep/trace.log, rootonly.txt, artifacts/out.cs, artifacts/sub/more.cs, lib/secret.cs
            Assert.Equal(6, u.Pruned);
        }
        finally { u.Text.Dispose(); u.Symbols.Dispose(); }
    }

    [Fact]
    public void Update_AfterAPrune_IndexesWhatTheIgnoreFilesNoLongerIgnore()
    {
        // The prune drops what the ignore files ignore from the ledger too: otherwise a later walk that no longer ignores
        // it finds each file unchanged against its ledger entry and never indexes it again.
        using var repo = Sample();
        repo.Write(".codecompass.json", "{ \"useIgnoreFiles\": false }");
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        repo.Write(".codecompass.json", "{ }");
        var p = RepositoryIndexer.PruneIgnored(repo.Root);
        p.Text.Dispose(); p.Symbols.Dispose();
        Assert.DoesNotContain("artifacts/out.cs", IndexedPaths(repo));

        repo.Write(".codecompass.json", "{ \"useIgnoreFiles\": false }");
        var u = RepositoryIndexer.Update(repo.Root);
        try
        {
            var paths = u.Text.AllPaths();
            Assert.Contains("artifacts/out.cs", paths);
            Assert.Contains("run.log", paths);
            Assert.Contains("lib/secret.cs", paths);
        }
        finally { u.Text.Dispose(); u.Symbols.Dispose(); }
    }

    [Fact]
    public void ApplyChanges_AnIgnoreFileSavedUnchanged_IsNoChange()
    {
        // An editor's save of the same text is not an ignore-file change: no prune pass, no reconcile, no stale note.
        using var repo = Sample();
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        var text = File.ReadAllText(repo.FullPath(".gitignore"));
        repo.Write(".gitignore", text);
        File.SetLastWriteTimeUtc(repo.FullPath(".gitignore"), DateTime.UtcNow.AddSeconds(5));
        var c = Apply(repo, ".gitignore");
        Assert.False(c.IgnoreFilesChanged);
    }

    [Fact]
    public void IgnoreFileNames_AreExact_InTheWalkAndTheTargetedUpdateAlike()
    {
        // As git reads them: ".Gitignore" is not an ignore file - judged the same by a walk and a watcher event, so the
        // two can't disagree about a file under it.
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A {}");
        repo.Write("sub/.Gitignore", "*.cs\n");
        repo.Write("sub/b.cs", "class B {}");
        Assert.Contains("sub/b.cs", Walked(repo.Root, new IgnoreRules(new RepoConfig()), 1));
        Assert.False(IgnoreFile.IsIgnoreFileName(".Gitignore"));
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        repo.Write("sub/c.cs", "class C {}");
        Apply(repo, "sub/c.cs");
        Assert.Contains("sub/c.cs", IndexedPaths(repo));
    }

    private static string AnswerWhen(Func<string> query, Func<string, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string a = query();
        while (!until(a) && DateTime.UtcNow < deadline) { Thread.Sleep(100); a = query(); }
        return a;
    }

    [Fact]
    public void Server_AnIgnoreFileChangedWhereNoWalkRuns_AnswersSaySoUntilAnUpdate()
    {
        // Where a change can't trigger a walk (autoReconcile off, as on a share or a huge repo), what the ignore file no
        // longer ignores isn't indexed: every answer says so until the index is rebuilt.
        using var repo = new TempRepo();
        repo.Write(".codecompass.json", "{ \"autoReconcile\": false }");
        repo.Write(".gitignore", "*.log\n");
        repo.Write("a.cs", "class AlphaNote {}");
        ServerContext.Init(repo.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.DoesNotContain(".gitignore/.ignore", CodeCompassTools.SearchCode("AlphaNote"));
            repo.Write(".gitignore", "*.tmp\n");
            ServerContext.OnChangesForTest(new ChangeBatch(new[] { repo.FullPath(".gitignore") }, FullReconcile: false));
            Assert.Contains(".gitignore/.ignore", AnswerWhen(() => CodeCompassTools.SearchCode("AlphaNote"), a => a.Contains(".gitignore/.ignore")));

            CodeCompassTools.Reindex();
            Assert.DoesNotContain(".gitignore/.ignore", CodeCompassTools.SearchCode("AlphaNote"));
        }
        finally { ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void Server_AnIgnoreFileChangedInALinkedRootWhereNoWalkRuns_AnswersNameThatRootUntilItsUpdated()
    {
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "class P { }\n");
        lib.Write(".codecompass.json", "{ \"autoReconcile\": false }");
        lib.Write(".gitignore", "*.log\n");
        lib.Write("lib.cs", "public class LibNote { }\n");
        var built = RepositoryIndexer.Build(lib.Root); built.Text.Dispose(); built.Symbols.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            // Loads the linked root, owned and watched here: only then does an edit there apply in this session.
            Assert.DoesNotContain(".gitignore/.ignore", CodeCompassTools.SearchCode("LibNote"));
            lib.Write(".gitignore", "*.tmp\n");
            ServerContext.OnLinkedChangesForTest(lib.Root, new ChangeBatch(new[] { lib.FullPath(".gitignore") }, FullReconcile: false));
            var during = AnswerWhen(() => CodeCompassTools.SearchCode("LibNote"), a => a.Contains(".gitignore/.ignore"));
            Assert.Contains(".gitignore/.ignore", during);
            Assert.Contains(Path.GetFileName(lib.Root), during);

            Thread.Sleep(20); // a build stamped strictly after the change
            var u = RepositoryIndexer.Update(lib.Root); u.Text.Dispose(); u.Symbols.Dispose();
            Assert.DoesNotContain(".gitignore/.ignore", AnswerWhen(() => CodeCompassTools.SearchCode("LibNote"), a => !a.Contains(".gitignore/.ignore")));
        }
        finally
        {
            ServerContext.Init(project.Root);
            LinkStore.Remove(project.Root, lib.Root);
        }
    }

    [Fact]
    public void Pattern_ALowerCaseClass_FoldsCaseWhereMatchingDoes()
    {
        // git folds the name before the bracket test (WM_CASEFOLD), so [[:lower:]] takes 'A' too on Windows and macOS.
        bool folds = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(folds, IgnoreFile.Parse("", new[] { "[[:lower:]]x" }).Match("Ax", isDir: false) == true);
        Assert.True(IgnoreFile.Parse("", new[] { "[[:lower:]]x" }).Match("ax", isDir: false) == true);
    }

    [Fact]
    public void ApplyChanges_AnIgnoreFileEdited_WithIgnoreFilesOff_IsNoIgnoreFileChange()
    {
        // Off, an ignore file is just a file: editing it mustn't start a reconcile or put a stale note on every answer.
        using var repo = Sample();
        repo.Write(".codecompass.json", "{ \"useIgnoreFiles\": false }");
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        repo.Write(".gitignore", "*.tmp\n");
        var c = Apply(repo, ".gitignore");
        Assert.False(c.IgnoreFilesChanged);
        Assert.Contains(".gitignore", IndexedPaths(repo)); // still indexed as a file
    }

    [Fact]
    public void ApplyChanges_ASymlinkedIgnoreFile_IsNotRead_AsTheWalkDoesNot()
    {
        using var repo = new TempRepo();
        using var outside = new TempRepo();
        outside.Write("everything", "*\n");
        repo.Write("a.cs", "class A {}");
        repo.Write("sub/x.cs", "class X {}");
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        try { File.CreateSymbolicLink(repo.FullPath("sub/.gitignore"), outside.FullPath("everything")); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // no symlink privilege on this box

        Assert.Contains("sub/x.cs", Walked(repo.Root, new IgnoreRules(new RepoConfig()), 1));
        repo.Write("sub/x.cs", "class X { int y; }");
        Apply(repo, "sub/.gitignore", "sub/x.cs");
        Assert.Contains("sub/x.cs", IndexedPaths(repo));
    }

    [Fact]
    public void PruneIgnored_StaleLedgerEntriesAlone_CountAsPruned()
    {
        // An index an older prune cleaned kept the ledger entries: dropping only those is still a change the server must
        // install (a no-change answer left its cached ledger to write them back).
        using var repo = Sample();
        repo.Write(".codecompass.json", "{ \"useIgnoreFiles\": false }");
        RepositoryIndexer.Build(repo.Root).Text.Dispose();
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
        try
        {
            foreach (var rel in text.AllPaths().ToList()) { text.RemovePath(rel); symbols.RemovePath(rel); }
            text.Flush(); symbols.Flush();
        }
        finally { text.Dispose(); symbols.Dispose(); }
        repo.Write(".codecompass.json", "{ }");

        var p = RepositoryIndexer.PruneIgnored(repo.Root);
        p.Text.Dispose(); p.Symbols.Dispose();
        Assert.True(p.Pruned > 0);
        using var snapshot = RepositoryIndexer.LoadSnapshot(repo.Root);
        Assert.False(snapshot.TryGetValue("artifacts/out.cs", out _));
        Assert.True(snapshot.TryGetValue("a.cs", out _));
    }

    [Fact]
    public void Server_ALinkedRootsReconcileAfterAnIgnoreFileChange_IsStoppedByAnUnlink()
    {
        // Like its startup reconcile: left running past an unlink with purge, it held the write lock against the purge and
        // could write the deleted index back.
        using var project = new TempRepo();
        using var lib = new TempRepo();
        project.Write("p.cs", "class P { }\n");
        lib.Write(".gitignore", "*.log\n");
        lib.Write("lib.cs", "public class LibRace { }\n");
        var built = RepositoryIndexer.Build(lib.Root); built.Text.Dispose(); built.Symbols.Dispose();
        LinkStore.Add(project.Root, lib.Root);
        var cacheDir = IndexStore.CacheDirPath(lib.Root);
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int reconciles = 0;
        bool sawCancel = false;
        ServerContext.LinkedReconcileHoldForTest = (root, ct) =>
        {
            if (!PathSafety.SameDir(root, lib.Root)) return;
            if (Interlocked.Increment(ref reconciles) == 1) { Thread.Sleep(1000); return; } // the startup one runs - slowly, as on CI
            parked.Set();
            try { release.Wait(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { sawCancel = true; throw; }
        };
        ServerContext.Init(project.Root);
        try
        {
            CodeCompassTools.Reindex();
            Assert.Contains("lib.cs", CodeCompassTools.SearchCode("LibRace")); // loaded + owned
            // Its startup reconcile done first: one still to walk would take the edit in, leaving the update nothing to do.
            Assert.True(ServerContext.LinkedLoadReconcileForTest(lib.Root)?.Wait(TimeSpan.FromSeconds(30)), "the startup reconcile never finished");
            lib.Write(".gitignore", "*.tmp\n");
            ServerContext.OnLinkedChangesForTest(lib.Root, new ChangeBatch(new[] { lib.FullPath(".gitignore") }, FullReconcile: false));
            Assert.True(parked.Wait(TimeSpan.FromSeconds(30)), "no reconcile ran after the ignore file changed");

            var msg = CodeCompassTools.ManageLinks("remove", lib.Root, purge: true);
            Assert.True(msg.Contains("index deleted"), msg);
            Assert.True(sawCancel, "the unlink didn't cancel the running reconcile");
            Thread.Sleep(500); // a reconcile that ran on would write the index back now
            Assert.False(Directory.Exists(cacheDir));
        }
        finally
        {
            release.Set();
            ServerContext.LinkedReconcileHoldForTest = null;
            ServerContext.Init(project.Root);
            LinkStore.Remove(project.Root, lib.Root);
            IndexStore.TryClearCacheDir(cacheDir, out _);
        }
    }
}
