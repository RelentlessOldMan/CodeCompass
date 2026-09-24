using System.Linq;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

public class ClangSemanticTests
{
    [Fact]
    public void FindReferences_IsSemantic_AcrossFiles()
    {
        using var repo = new TempRepo();
        repo.Write("add.cpp", """
        int add(int a, int b) { return a + b; }
        """);
        repo.Write("main.cpp", """
        int add(int a, int b);
        int main()
        {
            int r = add(1, 2);          // real call
            // call add again later
            const char* s = "add";
            return r;
        }
        """);

        var analyzer = new ClangCppAnalyzer(repo.Root);
        var refs = analyzer.FindReferences("add");

        // Only the real call is a reference; the comment and the "add" string are not,
        // and neither the forward declaration nor the definition count as references.
        var single = Assert.Single(refs);
        Assert.Equal("main.cpp", single.RelativePath);
        Assert.Contains("add(1, 2)", single.LineText);
    }

    [Fact]
    public void FindDefinitions_FindsTheDefinition_NotTheDeclaration()
    {
        using var repo = new TempRepo();
        repo.Write("add.cpp", "int add(int a, int b) { return a + b; }");
        repo.Write("main.cpp", "int add(int a, int b);\nint main() { return add(1,2); }");

        var analyzer = new ClangCppAnalyzer(repo.Root);
        var defs = analyzer.FindDefinitions("add");

        var def = Assert.Single(defs);
        Assert.Equal("add.cpp", def.RelativePath);
        Assert.Equal(1, def.Line);
    }

    [Fact]
    public void ClassAndMethod_References()
    {
        using var repo = new TempRepo();
        repo.Write("widget.cpp", """
        class Widget {
        public:
            void Run() { }
        };
        void use() {
            Widget w;
            w.Run();
        }
        """);

        var analyzer = new ClangCppAnalyzer(repo.Root);

        Assert.Single(analyzer.FindDefinitions("Widget"));   // the class definition
        var runRefs = analyzer.FindReferences("Run");
        Assert.Contains(runRefs, r => r.LineText.Contains("w.Run()"));
    }

    [Fact]
    public void CFile_WithC99Keyword_ParsesUnderCDialectNotCpp17()
    {
        // `restrict` is a C99 keyword and NOT valid C++; under the old -std=c++17 fallback a .c file using
        // it would fail to bind. With dialect-by-extension (gnu11 for .c) the definition resolves.
        using var repo = new TempRepo();
        repo.Write("driver.c", """
        void copy_block(int * restrict dst, const int * restrict src, int n)
        {
            for (int i = 0; i < n; i++) dst[i] = src[i];
        }
        """);

        var defs = new ClangCppAnalyzer(repo.Root).FindDefinitions("copy_block");
        var def = Assert.Single(defs);
        Assert.Equal("driver.c", def.RelativePath);
    }

    [Fact]
    public void ParseStats_ReportNoCompileDb_WhenNonePresent()
    {
        using var repo = new TempRepo();
        repo.Write("a.c", "int add(int a, int b) { return a + b; }");

        var analyzer = new ClangCppAnalyzer(repo.Root);
        _ = analyzer.FindReferences("add"); // triggers the build
        var stats = analyzer.Stats;

        Assert.False(stats.HasCompileDb);       // no compile_commands.json in the tree
        Assert.True(stats.SourceFilesSeen >= 1); // the .c TU was attempted
        Assert.False(stats.Capped);             // a tiny repo is far under the no-DB TU cap - full best-effort
    }

    [Fact]
    public void NoCompileDb_SmallRepo_StillResolvesSemantically_NotCapped()
    {
        // The no-DB cap must not touch ordinary small/medium repos: they parse on default flags and resolve.
        using var repo = new TempRepo();
        repo.Write("a.c", "int add(int a, int b) { return a + b; }");
        repo.Write("b.c", "int add(int a, int b);\nint use(void){ return add(1,2); }");

        var analyzer = new ClangCppAnalyzer(repo.Root);
        var refs = analyzer.FindReferences("add");
        Assert.Contains(refs, r => r.RelativePath == "b.c");  // real call resolved without a compile DB
        Assert.False(analyzer.Stats.Capped);
    }

    [Fact]
    public void ParseStats_ReportCompileDb_WhenPresent()
    {
        using var repo = new TempRepo();
        repo.Write("a.c", "int add(int a, int b) { return a + b; }");
        repo.Write("compile_commands.json", """
        [ { "directory": "<DIR>", "file": "a.c", "command": "clang -c a.c" } ]
        """.Replace("<DIR>", repo.Root.Replace("\\", "\\\\")));

        var analyzer = new ClangCppAnalyzer(repo.Root);
        _ = analyzer.FindReferences("add");
        Assert.True(analyzer.Stats.HasCompileDb);
        Assert.False(analyzer.Stats.Capped); // a compile DB means parse everything - never capped
    }

    [Fact]
    public void CompileDb_InNonDefaultDir_IsUsed_WhenConfigured()
    {
        using var repo = new TempRepo();
        repo.Write("a.c", "int add(int a, int b) { return a + b; }");
        // A compile DB in a nonstandard directory (not root, not root/build), plus config pointing at it.
        repo.Write("out/compile_commands.json",
            """[ { "directory": "<DIR>", "file": "a.c", "command": "clang -c a.c" } ]"""
            .Replace("<DIR>", repo.Root.Replace("\\", "\\\\")));
        repo.Write(".codecompass.json", """{ "compileCommands": ["out"] }""");

        var analyzer = new ClangCppAnalyzer(repo.Root);
        _ = analyzer.FindReferences("add"); // triggers the build (reads the configured DB)
        Assert.True(analyzer.Stats.HasCompileDb); // the out\ DB was located via config, not the default probe
    }

    [Fact]
    public void Dispose_ReleasesModel_AndRebuildsLazilyOnReuse()
    {
        using var repo = new TempRepo();
        repo.Write("add.cpp", "int add(int a, int b) { return a + b; }");
        repo.Write("main.cpp", "int add(int a, int b);\nint main() { return add(1, 2); }");

        var analyzer = new ClangCppAnalyzer(repo.Root);
        var before = analyzer.FindReferences("add").Select(r => (r.RelativePath, r.Line, r.Column, r.LineText)).ToList();
        Assert.NotEmpty(before);

        // Idle-eviction clears the resident semantic model; the next query must rebuild and match,
        // and the line text (read via a per-query cache now, not a resident field) must still resolve.
        analyzer.Dispose();
        var after = analyzer.FindReferences("add").Select(r => (r.RelativePath, r.Line, r.Column, r.LineText)).ToList();
        Assert.Equal(before, after);
        Assert.All(after, r => Assert.Contains("add(1, 2)", r.LineText));
    }
}
