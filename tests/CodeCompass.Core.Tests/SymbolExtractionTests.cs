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

    // Methods defined INSIDE a class body (field_identifier declarator) and out-of-class definitions (Value::method,
    // a qualified_identifier) are definitions too: find_definition must find them, and find_references - a C/C++ name
    // search - must not list them as uses. (Found comparing against clang on llvm: `bool isMustAlias() const {...}`
    // and `bool Value::hasNUsesOrMore(...) const {...}` were reported as references.)
    [Fact]
    public void Cpp_MemberDefinitions_InClassAndQualified()
    {
        const string src = """
        class Value {
        public:
            bool isMustAlias() const { return true; }
            bool hasNUsesOrMore(unsigned N) const;
        };
        bool Value::hasNUsesOrMore(unsigned N) const { return N > 0; }
        """;
        var syms = Extract("a.cpp", src);

        Assert.Contains(("isMustAlias", SymbolKind.Method), syms);
        Assert.Contains(("hasNUsesOrMore", SymbolKind.Method), syms);
    }

    [Fact]
    public void UnknownExtension_YieldsNothing()
    {
        Assert.Empty(Extract("notes.md", "# just text\nnothing to parse"));
    }
}
