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

    // Design decision from the generated-header field rounds: a firmware header is mostly #defines (register maps run to
    // hundreds of thousands), and minting each as a symbol bloated the symbol index and buried real definitions. #define
    // names are found by text search, not as symbols; functions in the same header still are. A silent change to the C
    // query would bring the bloat back, so it's pinned here.
    [Theory]
    [InlineData("regs.c")]     // C grammar
    [InlineData("regs.h")]     // headers parse with the C++ grammar - a separate query, pinned separately
    [InlineData("regs.cpp")]
    public void C_Defines_AreNotSymbols_FunctionsBesideThemAre(string file)
    {
        const string src = "#define REG_STATUS 0x40001000\n#define MAX_OF(a, b) ((a) > (b) ? (a) : (b))\n" +
                           "int real_fn(int x);\nint real_fn(int x) { return MAX_OF(x, REG_STATUS); }\n";
        var names = Extract(file, src).Select(s => s.Item1).ToArray();

        Assert.Contains("real_fn", names);
        Assert.DoesNotContain("REG_STATUS", names);
        Assert.DoesNotContain("MAX_OF", names);
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

    // A symbol is a DEFINITION, not every mention. Uses of a type (`struct node *n`, `sizeof(struct node)`, a parameter)
    // were captured as "Struct node" - so find_definition listed use sites and find_references (which skips definition
    // sites) dropped real uses: `refs node` returned 0. Only a specifier WITH A BODY defines the type.
    [Theory]
    [InlineData("a.c")]
    [InlineData("a.h")]   // headers go through the C++ grammar
    [InlineData("a.cpp")]
    public void C_TypeUses_AreNotDefinitions(string file)
    {
        const string src = """
        struct node { int v; struct node *next; };
        union u { int i; };
        enum color { RED };
        struct node *make(void);
        void node_free(struct node *n, union u x, enum color c) { }
        """;
        using var extractor = new TreeSitterSymbolExtractor();
        var syms = extractor.Extract(file, src).ToList();
        Assert.Single(syms, s => s.Name == "node");                 // only the struct with a body
        Assert.Equal(1, syms.Single(s => s.Name == "node").Line);
        Assert.Single(syms, s => s.Name == "u");
        Assert.Single(syms, s => s.Name == "color");
    }

    // A macro invocation followed by a block (list_for_each(pos, head) { ... }) parses as a function definition with no
    // return type in the C++ grammar. It's a use of the macro, not a definition.
    [Fact]
    public void Cpp_MacroLoopWithBlock_IsNotAFunctionDefinition()
    {
        const string src = """
        static inline void walk(struct list_head *head) {
            struct list_head *pos;
            list_for_each(pos, head) {
                touch(pos);
            }
        }
        """;
        var syms = Extract("list.h", src);
        Assert.Contains(("walk", SymbolKind.Function), syms);
        Assert.DoesNotContain(syms, s => s.Item1 == "list_for_each");
    }

    // Common definition shapes that weren't captured, so find_definition missed them and find_references listed them as
    // uses of themselves.
    [Fact]
    public void Cpp_MoreDefinitionShapes()
    {
        const string src = """
        static char *dup_name(const char *s) { return 0; }
        Value *IRBuilder::createAdd(Value *a) { return a; }
        int geo::Shape::perimeter() const { return 0; }
        int &Pool::slot(int i) { static int x; return x; }
        typedef struct { int x; } vec_t;
        union bits { int i; float f; };
        """;
        var syms = Extract("a.cpp", src);
        Assert.Contains(("dup_name", SymbolKind.Function), syms);
        Assert.Contains(("createAdd", SymbolKind.Method), syms);
        Assert.Contains(("perimeter", SymbolKind.Method), syms);
        Assert.Contains(("slot", SymbolKind.Method), syms);
        Assert.Contains(syms, s => s.Item1 == "vec_t");
        Assert.Contains(syms, s => s.Item1 == "bits");
    }

    // Review round 2, finding 1: requiring a return type (to reject macro-call-plus-block) also rejected constructors
    // defined in the class body, which have none - so find_definition missed them and refs listed them as uses.
    // Destructors and operators were never captured.
    [Fact]
    public void Cpp_ConstructorsDestructorsOperators_AreDefinitions()
    {
        const string src = """
        class Foo {
        public:
            Foo() : x(0) { }
            explicit Foo(int v) : x(v) { }
            template <typename T> Foo(T t, T u) { }
            ~Foo() { }
            Foo &operator=(const Foo &o) { return *this; }
            bool operator==(const Foo &o) const { return true; }
            int x;
        };
        Foo::~Foo() { }
        Foo &Foo::operator=(Foo &&o) { return *this; }
        """;
        using var extractor = new TreeSitterSymbolExtractor();
        var syms = extractor.Extract("a.cpp", src).ToList();
        Assert.Equal(new[] { 3, 4, 5 }, syms.Where(s => s.Name == "Foo" && s.Kind == SymbolKind.Method).Select(s => s.Line).OrderBy(l => l));
        Assert.Equal(new[] { 6, 11 }, syms.Where(s => s.Name == "~Foo").Select(s => s.Line).OrderBy(l => l));
        Assert.Equal(new[] { 7, 12 }, syms.Where(s => s.Name == "operator=").Select(s => s.Line).OrderBy(l => l));
        Assert.Contains(syms, s => s.Name == "operator==" && s.Line == 8);
    }

    // The in-class constructor rule must not let a macro-call-plus-block back in, at file scope or in a function body.
    [Fact]
    public void Cpp_TopLevelMacroWithBlock_IsNotADefinition()
    {
        const string src = """
        TEST(Suite, Name) { }
        void f() { FOREACH(x, xs) { use(x); } }
        """;
        var syms = Extract("t.cpp", src);
        Assert.DoesNotContain(syms, s => s.Item1 is "TEST" or "FOREACH");
        Assert.Contains(("f", SymbolKind.Function), syms);
    }

    // Finding 2: typedef'd pointer and function-pointer types, functions that return a function pointer, and types
    // marked with an export macro (`class LIB_API Klass {...}` - which the grammar reads as a function named Klass).
    [Theory]
    [InlineData("a.c")]
    [InlineData("a.cpp")]
    public void C_MoreTypedefAndFunctionShapes_AreDefinitions(string file)
    {
        const string src = """
        typedef struct node *node_p;
        typedef int (*cb_t)(int);
        void (*getHandler(int sig))(int) { return 0; }
        struct LIB_API Strk { int y; };
        """;
        using var extractor = new TreeSitterSymbolExtractor();
        var syms = extractor.Extract(file, src).ToList();
        Assert.Contains(syms, s => s.Name == "node_p" && s.Line == 1);
        Assert.Contains(syms, s => s.Name == "cb_t" && s.Line == 2);
        Assert.Contains(syms, s => s.Name == "getHandler" && s.Kind == SymbolKind.Function && s.Line == 3);
        Assert.Contains(syms, s => s.Name == "Strk" && s.Line == 4);
        Assert.DoesNotContain(syms, s => s.Name == "LIB_API");
    }

    [Fact]
    public void Cpp_ExportMacroClass_IsADefinition()
    {
        using var extractor = new TreeSitterSymbolExtractor();
        var syms = extractor.Extract("a.h", "class LIB_API Klass { int y; };\n").ToList();
        Assert.Contains(syms, s => s.Name == "Klass" && s.Kind == SymbolKind.Class);
        Assert.DoesNotContain(syms, s => s.Name == "LIB_API");
    }

    [Fact]
    public void C_PointerReturningFunction_IsADefinition()
    {
        var syms = Extract("a.c", "static char *dup_name(const char *s) { return 0; }\n");
        Assert.Contains(("dup_name", SymbolKind.Function), syms);
    }

    [Theory]
    [InlineData("impl.inl")]
    [InlineData("impl.ipp")]
    [InlineData("impl.tcc")]
    [InlineData("impl.h++")]
    [InlineData("impl.c++")]
    public void CppImplementationExtensions_AreParsed(string file)
    {
        var syms = Extract(file, "inline int omega_fn(int x) { return x; }\n");
        Assert.Contains(("omega_fn", SymbolKind.Function), syms);
    }

    // v1.0.242 regression: each extractor compiled its own query, and the C++ definition query takes ~0.9 s to compile.
    // The parallel build makes a fresh extractor for every loop replica (many per second), so llvm's build went from ~47 s
    // to ~285 s. Queries are compiled once per process now: ten new extractors cost one compile, not ten (~9 s).
    [Fact]
    public void ManyExtractors_CompileTheQueryOnce()
    {
        using (var warm = new TreeSitterSymbolExtractor()) warm.Extract("w.cpp", "int warm_fn() { return 0; }\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            using var x = new TreeSitterSymbolExtractor();
            Assert.Contains(x.Extract("a.cpp", $"int fn_{i}() {{ return {i}; }}\n"), s => s.Name == $"fn_{i}");
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"10 extractors took {sw.Elapsed.TotalSeconds:F1} s: the query is being recompiled");
    }

    [Fact]
    public void UnknownExtension_YieldsNothing()
    {
        Assert.Empty(Extract("notes.md", "# just text\nnothing to parse"));
    }
}
