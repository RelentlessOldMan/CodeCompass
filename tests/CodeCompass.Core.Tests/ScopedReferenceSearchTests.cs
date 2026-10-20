using System;
using System.Collections.Generic;
using System.Linq;
using CodeCompass.Semantics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// SEM-C2: a reference search covers only the documents whose text could hold a reference to the name, not the whole
// solution. The compiler also calls members without the text spelling them (foreach, await, deconstruction, collection
// initializers), `[Foo]` names FooAttribute, `new()` names no type, an escape hides a name and a global alias renames a
// type in every file; each must still be found, so the scoped answer has to equal the whole-solution one.
public class ScopedReferenceSearchTests
{
    private static List<string> Refs(RoslynCSharpAnalyzer a, string name) =>
        a.FindReferences(name, int.MaxValue).Select(l => $"{l.RelativePath}:{l.Line}:{l.Column}").OrderBy(s => s, StringComparer.Ordinal).ToList();

    private const string Declarations = """
using System; using System.Collections; using System.Collections.Generic; using System.Threading.Tasks; using System.Runtime.CompilerServices;
namespace P;
public class Seq { public Enumr GetEnumerator() => new Enumr(); }
public class Enumr { public int Current => 0; public bool MoveNext() => false; public void Dispose() {} }
public class ASeq { public AEnumr GetAsyncEnumerator(System.Threading.CancellationToken t = default) => new AEnumr(); }
public class AEnumr { public int Current => 0; public ValueTask<bool> MoveNextAsync() => default; public ValueTask DisposeAsync() => default; }
public class Pair { public void Deconstruct(out int a, out int b) { a = b = 0; } }
public class Awt { public Awtr GetAwaiter() => new Awtr(); }
public class Awtr : INotifyCompletion { public bool IsCompleted => true; public int GetResult() => 0; public void OnCompleted(Action a) {} }
public class Bag : IEnumerable<int> { public Bag() {} public void Add(int x) {} public IEnumerator<int> GetEnumerator() => null!; IEnumerator IEnumerable.GetEnumerator() => null!; }
public class Res : IDisposable { public void Dispose() {} }
public struct Num { public static Num operator +(Num a, Num b) => a; public static implicit operator int(Num n) => 0; }
public class Lst { public int Length => 0; public int this[int i] => 0; public Lst Slice(int s, int l) => this; }
public class Q { public Q Select(Func<int,int> f) => this; public Q Where(Func<int,bool> f) => this; }
public class Ctor { public Ctor() {} }
public class FooAttribute : Attribute {}
public delegate void Del();
[CollectionBuilder(typeof(CB), "Build")] public class Coll : IEnumerable<int> { public IEnumerator<int> GetEnumerator() => null!; IEnumerator IEnumerable.GetEnumerator() => null!; }
public static class CB { public static Coll Build(ReadOnlySpan<int> s) => new Coll(); }
[InterpolatedStringHandler] public ref struct Hnd { public Hnd(int a, int b) {} public void AppendLiteral(string s) {} public void AppendFormatted<T>(T t) {} }
public class Esc { }
public record Rec(int X);
public static class F
{
    public static Seq a() => null!; public static ASeq b() => null!; public static Pair c() => null!; public static Awt d() => null!;
    public static void e(Bag x) {} public static Res f() => null!; public static Num g() => default; public static Lst h() => null!;
    public static Q j() => null!; public static void k(Ctor c) {} public static Del l() => null!;
    public static void m(Coll c) {} public static void n(Hnd h) {} public static Rec r() => null!;
}
""";

    // Uses that never spell the declaring name.
    private const string Uses = """
using System; using System.Threading.Tasks; using P;
[Foo] class U
{
    async Task M()
    {
        foreach (var x in F.a()) { }
        await foreach (var x in F.b()) { }
        var (p, q) = F.c();
        if (F.c() is (1, 2)) { }
        int r = await F.d();
        F.e([1, 2]);
        using (F.f()) { }
        var n = F.g() + F.g(); int i = F.g();
        var l = F.h(); var v = l[^1]; var s = l[1..2]; var w = l[0];
        var qq = from x in F.j() where x > 0 select x + 1;
        F.k(null!);
        F.l()();
        F.m([1, 2, 3]);
        F.n($"x{1}");
        var (rx) = F.r(); if (F.r() is (3)) { }
        int? nn = null; (int, bool) tt = (1, true);
    }
}
""";

    private static readonly string[] Names =
    {
        "Seq", "Enumr", "GetEnumerator", "Current", "MoveNext", "Dispose", "ASeq", "GetAsyncEnumerator", "MoveNextAsync", "DisposeAsync",
        "Pair", "Deconstruct", "Awt", "GetAwaiter", "IsCompleted", "GetResult", "OnCompleted", "Bag", "Add", "Res", "Num", "op_Addition",
        "op_Implicit", "Lst", "Length", "Slice", "Item", "Q", "Select", "Where", "Ctor", "FooAttribute", "Del", "Invoke", "Coll", "CB",
        "Build", "Hnd", "AppendLiteral", "AppendFormatted", "Esc", "Rec", "X", "F", "a", "k", "Int32", "Boolean", "Nullable",
        "ValueTuple", "Index", "Range", "Object", "get_X", "get_Length",
    };

    [Fact]
    public void EveryName_FindsTheSameReferences_AsAWholeSolutionSearch()
    {
        using var repo = new TempRepo();
        repo.Write("decl.cs", Declarations);
        repo.Write("u.cs", Uses);
        repo.Write("esc.cs", "class UseIt { P.\\u0045sc e; }\n");
        repo.Write("other.cs", "class Other { void M() { } }\n");
        // Kept apart from u.cs: a file holding `new(` is searched for every type, which would hide a type's other misses.
        repo.Write("new.cs", "class UN { void M() { P.F.e(new() { 1, 2 }); P.F.k(new ()); } }\n");
        // A search for a type cascades to its constructors, and `base(1)` calls one without naming the type.
        repo.Write("anim.cs", "namespace P; public class Animal { public Animal(int x) {} public Animal() {} } public static class Cee { public static void Bar() {} }\n");
        repo.Write("d1.cs", "namespace P; public partial class Dog : Animal { }\n");
        repo.Write("d2.cs", "namespace P; public partial class Dog { public Dog() : base(1) { } }\n");
        // A format character (here a soft hyphen) inside an identifier is dropped: `Ce­e` is Cee.
        repo.Write("cf.cs", "namespace P; class UseCf { void M() { Ce­e.Bar(); } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);

        foreach (var name in Names.Concat(new[] { "Animal", "Dog", "Cee", "Bar" }))
        {
            analyzer.ScopeSearches = true;
            var scoped = Refs(analyzer, name);
            analyzer.ScopeSearches = false;
            var whole = Refs(analyzer, name);
            Assert.True(whole.SequenceEqual(scoped), $"{name}: scoped [{string.Join(", ", scoped)}] vs whole [{string.Join(", ", whole)}]");
        }

        // The cases that need more than the name all have answers here, so the differential above exercises each rule.
        analyzer.ScopeSearches = true;
        Assert.Contains("esc.cs:1:17", Refs(analyzer, "Esc"));
        Assert.Contains("u.cs:2:2", Refs(analyzer, "FooAttribute"));
        Assert.Contains("new.cs:1:29", Refs(analyzer, "Bag"));
        Assert.Contains("new.cs:1:52", Refs(analyzer, "Ctor"));
        Assert.Contains("u.cs:6:9", Refs(analyzer, "GetEnumerator"));
        Assert.Contains("u.cs:8:9", Refs(analyzer, "Deconstruct"));
        Assert.Contains("u.cs:10:17", Refs(analyzer, "GetAwaiter"));
        Assert.Contains("new.cs:1:37", Refs(analyzer, "Add"));
        Assert.Contains("d2.cs:1:56", Refs(analyzer, "Animal"));
        Assert.Contains("cf.cs:1:39", Refs(analyzer, "Cee"));
    }

    [Fact]
    public void ARareName_SearchesOnlyTheFilesThatCouldHoldIt()
    {
        using var repo = new TempRepo();
        repo.Write("decl.cs", "public class Pair { public void Swap() { } }\n");
        repo.Write("use.cs", "class U { void M(Pair p) { p.Swap(); var x = new Pair(); } }\n");
        repo.Write("new.cs", "class N { object o = new(); }\n");
        repo.Write("other.cs", "class Other { void M() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);

        Assert.Equal(new[] { "use.cs:1:30" }, Refs(analyzer, "Swap"));
        Assert.Equal(2, analyzer.LastSearchScope); // decl.cs, use.cs
        Assert.Equal(new[] { "use.cs:1:18", "use.cs:1:50" }, Refs(analyzer, "Pair"));
        Assert.Equal(3, analyzer.LastSearchScope); // and new.cs: a type can be created by `new()`
    }

    // `[Foo]` names FooAttribute: the file holding it must be searched though it never spells the full name.
    [Fact]
    public void AnAttribute_IsSearchedUnderItsShortName()
    {
        using var repo = new TempRepo();
        repo.Write("decl.cs", "public class FooAttribute : System.Attribute { }\n");
        repo.Write("use.cs", "[Foo] class U { }\n");
        repo.Write("other.cs", "class Other { }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Equal(new[] { "use.cs:1:2" }, Refs(analyzer, "FooAttribute"));
        Assert.Equal(2, analyzer.LastSearchScope);
    }

    // A repo that polyfills System.Index / System.Range declares them in source, and `^1` / `1..2` name them without
    // spelling them: such a name searches everything, and finds what a whole-solution search finds.
    [Fact]
    public void APolyfilledKeywordType_FindsTheSameReferences_AsAWholeSolutionSearch()
    {
        using var repo = new TempRepo();
        repo.Write("poly.cs", """
namespace System
{
    public readonly struct Index { public Index(int v, bool fromEnd = false) { } public static implicit operator Index(int v) => default; public int GetOffset(int l) => 0; }
    public readonly struct Range { public Range(Index s, Index e) { } public Index Start => default; public Index End => default; }
}
""");
        repo.Write("use.cs", "class U { int[] a = { 1, 2 }; void M() { var x = a[^1]; var y = a[0..1]; } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        foreach (var name in new[] { "Index", "Range" })
        {
            analyzer.ScopeSearches = true;
            var scoped = Refs(analyzer, name);
            Assert.Equal(-1, analyzer.LastSearchScope);
            analyzer.ScopeSearches = false;
            Assert.Equal(Refs(analyzer, name), scoped);
        }
    }

    [Fact]
    public void ANameTheCompilerCallsByPattern_SearchesEverything()
    {
        using var repo = new TempRepo();
        repo.Write("decl.cs", "public class Seq { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => null!; }\n");
        repo.Write("use.cs", "class U { void M(Seq s) { foreach (var x in s) { } } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Equal(new[] { "use.cs:1:27" }, Refs(analyzer, "GetEnumerator"));
        Assert.Equal(-1, analyzer.LastSearchScope);
    }

    // A global alias renames a type in every file, and its uses don't spell the type: any one searches everything.
    [Fact]
    public void AGlobalAlias_SearchesEverything()
    {
        using var repo = new TempRepo();
        repo.Write("decl.cs", "namespace P; public class Target { }\n");
        repo.Write("g.cs", "global using GA = P.Target;\n");
        repo.Write("use.cs", "class U { GA ga = null!; }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Equal(new[] { "g.cs:1:21", "use.cs:1:11" }, Refs(analyzer, "Target"));
        Assert.Equal(-1, analyzer.LastSearchScope);
    }

    // What a file's text says about scope is cached per file; an in-place edit has to drop it, or a `new()` added since is
    // never searched.
    [Fact]
    public void AnEditedFile_IsJudgedOnItsNewText()
    {
        using var repo = new TempRepo();
        repo.Write("decl.cs", "public class Ctor { } public static class F { public static void K(Ctor c) { } }\n");
        repo.Write("b.cs", "class B { void M() { } }\n");
        using var analyzer = new RoslynCSharpAnalyzer(repo.Root);
        Assert.Equal(new[] { "decl.cs:1:68" }, Refs(analyzer, "Ctor"));

        repo.Write("b.cs", "class B { void M() { F.K(new()); } }\n");
        Assert.True(analyzer.TryApplyChanges(new[] { (repo.FullPath("b.cs"), true) }));
        Assert.Equal(new[] { "b.cs:1:26", "decl.cs:1:68" }, Refs(analyzer, "Ctor"));
    }

    [Theory]
    [InlineData("var x = new();", true)]
    [InlineData("var x = new ( );", true)]
    [InlineData("var x = new\r\n    ();", true)]
    [InlineData("var x = new /* c */ ();", true)]  // fails open on a comment
    [InlineData("var x = new Foo();", false)]
    [InlineData("var x = new[] { 1 };", false)]
    [InlineData("var x = 1; // renewal", false)]
    [InlineData("new", false)]
    [InlineData("", false)]
    public void TargetTypedNew_IsSpotted(string text, bool expected) =>
        Assert.Equal(expected, RoslynCSharpAnalyzer.HasTargetTypedNew(SourceText.From(text)));

    [Theory]
    [InlineData("P.\\u0045sc e;", true)]
    [InlineData("P.\\U00000045sc e;", true)]
    [InlineData("Ce\u00ADe.Bar();", true)]        // soft hyphen
    [InlineData("a\u200Db", true)]                // zero-width joiner
    [InlineData("var p = \"C:\\\\Users\";", false)] // a backslash and a u, but no hex digits
    [InlineData("x \\u12", false)]
    [InlineData("// see \\update_notes", false)]       // enough characters follow, but they aren't hex
    [InlineData("// café, 😀", false)]          // non-ASCII that isn't a format character
    [InlineData("", false)]
    public void HiddenSpelling_IsSpotted(string text, bool expected) =>
        Assert.Equal(expected, RoslynCSharpAnalyzer.HasHiddenSpelling(SourceText.From(text)));

    [Theory]
    [InlineData(65_536 - 1)]
    [InlineData(65_536)]
    public void HiddenSpelling_IsSpotted_AcrossAChunkBoundary(int at)
    {
        Assert.True(RoslynCSharpAnalyzer.HasHiddenSpelling(SourceText.From(new string('x', at) + "\\u0045")));
        Assert.True(RoslynCSharpAnalyzer.HasHiddenSpelling(SourceText.From(new string('x', at - 3) + "\\u0045")));
        Assert.True(RoslynCSharpAnalyzer.HasHiddenSpelling(SourceText.From(new string('x', at) + "\u00AD")));
        Assert.True(RoslynCSharpAnalyzer.HasHiddenSpelling(SourceText.From(new string('x', at) + "\U000E0001"))); // a surrogate pair
        Assert.False(RoslynCSharpAnalyzer.HasHiddenSpelling(SourceText.From(new string('x', at) + "😀" + new string('x', 10))));
    }

    [Theory]
    [InlineData("public Dog() : base(1) { }", true)]
    [InlineData("public Dog() : base (1) { }", true)]
    [InlineData("base.M();", false)]
    [InlineData("var database = 1;", false)]
    public void BaseCall_IsSpotted(string text, bool expected) =>
        Assert.Equal(expected, RoslynCSharpAnalyzer.HasKeywordCall(SourceText.From(text), "base"));

    [Theory]
    [InlineData(65_536 - 4)]  // `new` ends at the boundary; `(` is in the next chunk
    [InlineData(65_536 - 3)]  // `new` ends exactly at the chunk end
    [InlineData(65_536 - 2)]  // `new` straddles the boundary
    [InlineData(65_536 - 1)]
    [InlineData(65_536)]
    [InlineData(200_000)]
    public void TargetTypedNew_IsSpotted_AcrossAChunkBoundary(int at)
    {
        Assert.True(RoslynCSharpAnalyzer.HasTargetTypedNew(SourceText.From(new string('x', at) + "new()" + new string('x', 70_000))));
        Assert.True(RoslynCSharpAnalyzer.HasTargetTypedNew(SourceText.From(new string('x', at) + "new" + new string(' ', 10) + "()")));
        Assert.False(RoslynCSharpAnalyzer.HasTargetTypedNew(SourceText.From(new string('x', at) + "new Foo()" + new string('x', 70_000))));
    }
}
