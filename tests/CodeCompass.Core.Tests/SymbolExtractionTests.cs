using System.Linq;
using CodeCompass.Core.Symbols;
using Xunit;

namespace CodeCompass.Core.Tests;

public class SymbolExtractionTests
{
    private static (string, SymbolKind)[] Extract(string relPath, string text)
    {
        using var extractor = new TreeSitterSymbolExtractor();
        return extractor.Extract(relPath, text).Select(s => (s.Name, s.Kind)).ToArray();
    }

    [Fact]
    public void CSharp_Definitions()
    {
        const string src = """
        namespace N.Sub
        {
            public class Foo
            {
                public int Bar { get; set; }
                public void Baz() { }
                public Foo() { }
            }
            public interface IThing { }
            public enum Color { Red }
            public struct Vec { }
        }
        """;
        var syms = Extract("a.cs", src);

        Assert.Contains(("N.Sub", SymbolKind.Namespace), syms);
        Assert.Contains(("Foo", SymbolKind.Class), syms);
        Assert.Contains(("Bar", SymbolKind.Property), syms);
        Assert.Contains(("Baz", SymbolKind.Method), syms);
        Assert.Contains(("IThing", SymbolKind.Interface), syms);
        Assert.Contains(("Color", SymbolKind.Enum), syms);
        Assert.Contains(("Vec", SymbolKind.Struct), syms);
    }

    [Fact]
    public void Python_Definitions()
    {
        const string src = """
        class Bar:
            def method_a(self):
                pass

        def top_func():
            pass
        """;
        var syms = Extract("a.py", src);

        Assert.Contains(("Bar", SymbolKind.Class), syms);
        Assert.Contains(("method_a", SymbolKind.Function), syms);
        Assert.Contains(("top_func", SymbolKind.Function), syms);
    }

    [Fact]
    public void C_Definitions()
    {
        const string src = """
        struct Point { int x; int y; };
        enum Status { OK };
        int add(int a, int b) { return a + b; }
        """;
        var syms = Extract("a.c", src);

        Assert.Contains(("Point", SymbolKind.Struct), syms);
        Assert.Contains(("Status", SymbolKind.Enum), syms);
        Assert.Contains(("add", SymbolKind.Function), syms);
    }

    [Fact]
    public void Cpp_Definitions()
    {
        const string src = """
        namespace ns {
            class Widget {
            public:
                void run();
            };
        }
        int compute() { return 0; }
        """;
        var syms = Extract("a.cpp", src);

        Assert.Contains(("ns", SymbolKind.Namespace), syms);
        Assert.Contains(("Widget", SymbolKind.Class), syms);
        Assert.Contains(("compute", SymbolKind.Function), syms);
    }

    [Fact]
    public void UnknownExtension_YieldsNothing()
    {
        Assert.Empty(Extract("notes.md", "# just text\nnothing to parse"));
    }
}
