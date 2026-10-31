using System;
using System.Collections.Generic;
using System.Linq;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// The bitset-backed TrigramSet must give exactly the distinct set the old per-char HashSet<long> gave, for every
// kind of text, and a reused set must not leak one file's trigrams into the next.
public class TrigramSetTests
{
    private static long[] Reference(string text)
    {
        var set = new HashSet<long>();
        for (int i = 0; i + 3 <= text.Length; i++) set.Add(TrigramIndex.TriKey(text[i], text[i + 1], text[i + 2]));
        return set.OrderBy(x => x).ToArray();
    }

    private static string Random(Random rnd, int length, Func<Random, char> next)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = next(rnd);
        return new string(chars);
    }

    public static IEnumerable<object[]> Texts()
    {
        var rnd = new Random(20261019);
        yield return new object[] { "" };
        yield return new object[] { "a" };
        yield return new object[] { "ab" };
        yield return new object[] { "abc" };
        yield return new object[] { "line one\r\nline two\r\n\tindented\n" };
        yield return new object[] { "\u007F\u007F\u007F\u0080\u007F\u007F\u0000\u0000\u0000\u007F" }; // the 127/128 edge
        yield return new object[] { "xéy café naïve 中文 abc" };                   // mixed: ASCII + BMP
        yield return new object[] { "emoji 😀 and 😁 pairs 😀" };          // surrogate pairs
        yield return new object[] { Random(rnd, 20_000, r => (char)r.Next(128)) };                         // all ASCII incl. controls
        yield return new object[] { Random(rnd, 20_000, r => (char)(r.Next(4) == 0 ? r.Next(0x80, 0x3000) : r.Next(32, 127))) };
        yield return new object[] { Random(rnd, 20_000, r => (char)r.Next(0x10000)) };                     // any UTF-16 unit
        yield return new object[] { string.Concat(Enumerable.Repeat("public static void Main(string[] args) { }\n", 25_000)) }; // ~1 MB repetitive
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void ComputeTrigrams_MatchesTheHashSetReference(string text)
    {
        Assert.Equal(Reference(text), TrigramIndex.ComputeTrigrams(text).OrderBy(x => x).ToArray());
        Assert.Equal(Reference(text), TrigramIndex.ComputeTrigrams(text, new TrigramSet()).OrderBy(x => x).ToArray());
    }

    // A reused set clears only the bits it set: if it missed one, the next file would lose that trigram (seen as a repeat),
    // and a search for it there would come back empty.
    [Fact]
    public void AReusedSet_GivesEachTextExactlyItsOwnTrigrams()
    {
        var rnd = new Random(7);
        var texts = Texts().Select(o => (string)o[0]).ToList();
        for (int i = 0; i < 40; i++) texts.Add(Random(rnd, rnd.Next(0, 3000), r => (char)(r.Next(10) == 0 ? r.Next(0x80, 0x800) : r.Next(97, 101))));

        var scratch = new TrigramSet();
        foreach (var text in texts.Concat(texts))
        {
            Assert.Equal(Reference(text), TrigramIndex.ComputeTrigrams(text, scratch).OrderBy(x => x).ToArray());
            Assert.Equal(0, scratch.Count);
        }
    }

    // A build worker that hits OutOfMemoryException skips the file and is reused (RepositoryIndexer's memory backstop),
    // so a set that failed mid-file must come back clean: no trigram of the failed file left over, and no bit left set
    // without its list entry - that trigram would read as a repeat, and every later file on the worker would lose it.
    [Fact]
    public void ASetThatFailedMidFile_IndexesTheNextFilesExactly()
    {
        var rnd = new Random(11);
        var big = Random(rnd, 20_000, r => (char)r.Next(32, 127)); // well past the first-seen list's initial size
        var small = "class Foo { int bar; } // é";
        var scratch = new TrigramSet();

        scratch.BeforeGrowForTest = () => throw new OutOfMemoryException();
        Assert.Throws<OutOfMemoryException>(() => TrigramIndex.ComputeTrigrams(big, scratch));
        scratch.BeforeGrowForTest = null;

        Assert.Equal(Reference(small), TrigramIndex.ComputeTrigrams(small, scratch).OrderBy(x => x).ToArray());
        Assert.Equal(Reference(big), TrigramIndex.ComputeTrigrams(big, scratch).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void EveryAsciiTrigramIsDistinguished()
    {
        // Spot-check the 7-bit packing at its edges: triples that differ in one char's top or bottom bit.
        var set = new TrigramSet();
        char[] edge = { '\0', '\u0001', '@', '?', '~', '\u007F' };
        var expected = new HashSet<long>();
        foreach (var a in edge) foreach (var b in edge) foreach (var c in edge)
                {
                    set.Add(a, b, c);
                    set.Add(a, b, c);
                    expected.Add(TrigramIndex.TriKey(a, b, c));
                }
        Assert.Equal(expected.OrderBy(x => x), set.Drain(int.MaxValue).OrderBy(x => x));
    }
}
