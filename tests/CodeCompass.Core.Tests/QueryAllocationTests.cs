using System.IO.MemoryMappedFiles;
using System.Text;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// The query-path allocation cuts (mapped names compared in place, span path walks, one candidate collection per refs
// call) must answer exactly as the code they replaced.
public sealed class QueryAllocationTests
{
    // The columns are sorted by string.CompareOrdinal - UTF-16 order - so a mapped name must be decoded to compare, not
    // compared as UTF-8 bytes: those order a non-BMP char (F0..) after U+E000-U+FFFF (EE/EF..), UTF-16 before (D8..).
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("abc", "abd")]
    [InlineData("abd", "abc")]
    [InlineData("ab", "abc")]
    [InlineData("abc", "ab")]
    [InlineData("", "a")]
    [InlineData("a", "")]
    [InlineData("Zed", "apple")]
    [InlineData("�", "\U0001F600")]
    [InlineData("\U0001F600", "�")]
    [InlineData("src/.cs", "src/\U00010000.cs")]
    [InlineData("café/x.cs", "cafe/x.cs")]
    public void MappedCompare_IsStringCompareOrdinal(string mapped, string key)
        => Assert.Equal(Math.Sign(string.CompareOrdinal(mapped, key)), Map(mapped, (v, len) => MappedUtf8.CompareOrdinal(v, 3, len, key)));

    [Fact]
    public void MappedCompare_PastTheStackBuffer_IsStillExact()
    {
        var a = new string('x', 300) + "\U0001F600" + new string('y', 700);
        var b = new string('x', 300) + "�" + new string('y', 700);
        Assert.Equal(Math.Sign(string.CompareOrdinal(a, b)), Map(a, (v, len) => MappedUtf8.CompareOrdinal(v, 3, len, b)));
        Assert.Equal(0, Map(a, (v, len) => MappedUtf8.CompareOrdinal(v, 3, len, a)));
    }

    [Theory]
    [InlineData("WidgetFactory", "widget", true)]
    [InlineData("WidgetFactory", "FACTORY", true)]
    [InlineData("WidgetFactory", "gadget", false)]
    [InlineData("CafÉBar", "café", true)]
    [InlineData("x\U0001F600y", "\U0001F600", true)]
    public void MappedContains_IsStringContainsIgnoreCase(string mapped, string value, bool expected)
    {
        Assert.Equal(expected, mapped.Contains(value, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, Map(mapped, (v, len) => MappedUtf8.Contains(v, 3, len, value, StringComparison.OrdinalIgnoreCase)));
    }

    // Writes `text` as UTF-8 at offset 3 of a mapped file (an unaligned start, as in a real column) and runs `f` over it.
    private static T Map<T>(string text, Func<MemoryMappedViewAccessor, int, T> f)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var path = Path.Combine(Path.GetTempPath(), "cc-mu8-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 }.Concat(bytes).Concat(new byte[] { 9 }).ToArray());
        try
        {
            using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return f(view, bytes.Length);
        }
        finally { File.Delete(path); }
    }

    // The span walk against the Split it replaced, on the shapes a split treats specially: empty segments, leading and
    // trailing separators, a lone separator, dots.
    [Theory]
    [InlineData("a//node_modules/x.js")]
    [InlineData("/node_modules/x.js")]
    [InlineData("node_modules/")]
    [InlineData("src/bin/")]
    [InlineData("/")]
    [InlineData("\\\\")]
    [InlineData("src\\obj\\x.cs")]
    [InlineData("src/x.PNG")]
    [InlineData("src/.png")]
    [InlineData("src/x.")]
    [InlineData("src.png/x.cs")]
    [InlineData("NODE_MODULES/x")]
    [InlineData("a/b/c")]
    public void IsIgnoredPath_MatchesTheSplitItReplaced(string rel)
    {
        var rules = new IgnoreRules();
        var parts = rel.Split('/', '\\');
        bool expected = parts.Take(parts.Length - 1).Any(p => p.Length > 0 && rules.IsIgnoredPath(p + "/f.cs"))
                        || rules.IsIgnoredPath("f" + Path.GetExtension(parts[^1]));
        Assert.Equal(expected, rules.IsIgnoredPath(rel));
    }

    [Theory]
    [InlineData("..", true)]
    [InlineData("../x.cs", true)]
    [InlineData("a/../x.cs", true)]
    [InlineData("a\\..\\x.cs", true)]
    [InlineData("a/..", true)]
    [InlineData("..foo/x.cs", false)]
    [InlineData("a/foo../x.cs", false)]
    [InlineData("a/.../x.cs", false)]
    [InlineData("a//b.cs", false)]
    [InlineData("a/b.cs", false)]
    [InlineData("", true)]
    [InlineData(".", true)]
    public void IsOutsideRepo_MatchesTheSplitItReplaced(string rel, bool expected)
    {
        Assert.Equal(rel.Length == 0 || rel == "." || Path.IsPathRooted(rel) || rel.Split('/', '\\').Any(p => p == ".."), expected);
        Assert.Equal(expected, PathSafety.IsOutsideRepo(rel));
    }

    // find_references reuses CandidateFiles' collection for its name search. Same answer when reused; ignored once the
    // index changed in between, or for another query.
    [Fact]
    public void ACollectedCandidateList_IsReusedOnlyForItsOwnQueryAndIndexVersion()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "namespace N { class Foo { void Run() { } } }");
        repo.Write("b.cs", "namespace N { class Bar { void Run() { } } }");
        var dir = Path.Combine(Path.GetTempPath(), "cc-idx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var idx = SegmentedIndex.Create(repo.Root, dir, 64);
            foreach (var (rel, full) in repo.Docs()) idx.AddDocumentText(rel, File.ReadAllText(full));
            idx.Flush();

            static string Shape(IReadOnlyList<SearchMatch> ms) => string.Join(";", ms.Select(m => $"{m.Path}:{m.Line}:{m.Column}").OrderBy(s => s));

            Assert.Equal(2, idx.CandidateFiles("Run", out var collected).Count);
            Assert.NotNull(collected);
            Assert.Equal(Shape(idx.Search("Run", 1000)), Shape(idx.Search("Run", 1000, collected: collected)));
            Assert.Equal(Shape(idx.Search("Foo", 1000)), Shape(idx.Search("Foo", 1000, collected: collected))); // another query

            repo.Write("c.cs", "namespace N { class Baz { void Run() { } } }");
            idx.AddDocumentText("c.cs", File.ReadAllText(repo.FullPath("c.cs")));
            Assert.Contains(idx.Search("Run", 1000, collected: collected), m => m.Path == "c.cs"); // stale: collected again

            idx.CandidateFiles("Run", out collected);
            idx.RemovePath("a.cs");
            Assert.DoesNotContain(idx.Search("Run", 1000, collected: collected), m => m.Path == "a.cs");
        }
        finally { Directory.Delete(dir, true); }
    }
}
