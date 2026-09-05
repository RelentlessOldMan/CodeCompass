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
}
