using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Semantics;
using Xunit;

namespace CodeCompass.Core.Tests;

// A broad C/C++ find_references over a 1 GB corpus (200 files of 2-8 MB, one hot name in each) took 14 s against
// 5.7 s to index the same bytes. The profile: the name search read each hit file whole, then the comment/string
// filter read the SAME file again and lexed it one char at a time. These pin the three fixes:
//  - the filter classifies from the text the search already read (no second read of the file);
//  - SourceFile.ReadAllText decodes exactly like StreamReader (it now decodes the bytes in one pass);
//  - the faster C/C++ scanner returns exactly the spans the old char-at-a-time scanner did.
[Collection("compaction-env")] // serializes the CODECOMPASS_FORCE_NETWORK env mutation
public class RefsReadOnceTests
{
    // --- read once ---------------------------------------------------------------------------------------------

    private static List<string> RunRefs(TempRepo repo, string name, LexicalSpanFilter filter, bool handOffText)
    {
        var (text, symbols, _) = RepositoryIndexer.Build(repo.Root);
        using (text) using (symbols)
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            listed.UnionWith(ReferenceMerge.DefinitionKeys(symbols.FindByName(name), rel => rel));
            var accept = ReferenceMerge.ReferenceAccept(repo.Root, name.Length, csharpIncomplete: false, filter, listed, rel => rel);
            return text.Search(name, 1000, maxPerFile: ReferenceMerge.MaxLexicalHitsPerFile, orderByPath: true,
                               pathFilter: ReferenceMerge.ReferencePathFilter(false), accept: accept,
                               textSink: handOffText ? ReferenceMerge.ReferenceTextSink(repo.Root, filter) : null)
                       .Select(m => $"{m.Path}:{m.Line}:{m.Column}").ToList();
        }
    }

    private static TempRepo CFiles()
    {
        var repo = new TempRepo();
        repo.Write("hot.c", "int hot_name(int x) { return x; }\n");
        for (int i = 0; i < 6; i++)
            repo.Write($"use{i}.c",
                $"/* hot_name in a comment */\nint u{i}(void) {{ return hot_name({i}); }}\n" +
                $"const char *s{i} = \"hot_name\"; // hot_name\nint w{i} = hot_name(2) + hot_name(3);\n");
        repo.Write("notes.txt", "hot_name is mentioned in prose\n");
        return repo;
    }

    [Fact]
    public void NameSearch_ClassifiesFromTheTextItAlreadyRead_NoSecondRead()
    {
        using var repo = CFiles();

        var plain = new LexicalSpanFilter("hot_name");
        var expected = RunRefs(repo, "hot_name", plain, handOffText: false);
        Assert.Equal(6, plain.FilesReadFromDisk); // without the hand-off, every use file is read a second time

        var shared = new LexicalSpanFilter("hot_name");
        var got = RunRefs(repo, "hot_name", shared, handOffText: true);

        Assert.Equal(18, expected.Count);  // 3 real uses per file; comments, strings and the definition are dropped
        Assert.Equal(expected, got);       // same answer, same order
        Assert.Equal(6, shared.FilesClassified);
        Assert.Equal(0, shared.FilesReadFromDisk);
    }

    [Fact]
    public void OfferedText_IsUsedOnlyForItsOwnPath()
    {
        using var repo = new TempRepo();
        repo.Write("a.c", "int a = hot_name(1);\n");
        repo.Write("b.c", "// hot_name\nint b = hot_name(2);\n");
        var filter = new LexicalSpanFilter("hot_name");

        // a.c's text is offered; a hit in b.c must still be classified from b.c's own content.
        filter.Offer(repo.FullPath("a.c"), File.ReadAllText(repo.FullPath("a.c")));
        Assert.True(filter.IsInCommentOrString(repo.FullPath("b.c"), 1, 4));
        Assert.False(filter.IsInCommentOrString(repo.FullPath("b.c"), 2, 9));
        Assert.Equal(1, filter.FilesReadFromDisk);
        Assert.False(filter.IsInCommentOrString(repo.FullPath("a.c"), 1, 9));
        Assert.Equal(1, filter.FilesReadFromDisk); // a.c came from the offer
    }

    [Fact]
    public void Mcp_FindReferences_ClassifiesFromTheTextItAlreadyRead()
    {
        // The MCP tool wires the hand-off itself (CodeCompassTools, not the CLI), and agents use that path.
        using var repo = CFiles();
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            CodeCompass.Mcp.CodeCompassTools.Reindex();
            int classified = LexicalSpanFilter.AllFilesClassified, fromDisk = LexicalSpanFilter.AllFilesReadFromDisk;

            var r = CodeCompass.Mcp.CodeCompassTools.FindReferences("hot_name");

            for (int i = 0; i < 6; i++)
            {
                Assert.Contains($"use{i}.c:2:", r);                // the real use...
                Assert.DoesNotContain($"use{i}.c:1:", r);          // ...not the comment
                Assert.DoesNotContain($"use{i}.c:3:", r);          // ...nor the string and line comment
            }
            Assert.Equal(6, LexicalSpanFilter.AllFilesClassified - classified);
            Assert.Equal(0, LexicalSpanFilter.AllFilesReadFromDisk - fromDisk);
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void HandedOffText_IsNotHeldPastItsFile_WhenNothingClassifiesIt()
    {
        // A file the filter never classifies - not a C-family/C# file, or every hit rejected before the comment check
        // (a longer identifier) - must not leave its text (up to 128 MB) held for the rest of the query.
        using var repo = new TempRepo();
        repo.Write("a.c", "int a = hot_name(1);\n");
        repo.Write("z_longer.c", "int z = hot_name_longer(1);\n");
        repo.Write("zz.py", "hot_name(2)\n");
        var filter = new LexicalSpanFilter("hot_name");

        var got = RunRefs(repo, "hot_name", filter, handOffText: true);

        Assert.Equal(new[] { "a.c:1:9", "zz.py:1:1" }, got);
        Assert.False(filter.HoldsOfferedText);
    }

    [Fact]
    public void ParallelVerify_KeptTexts_AreBoundedAndReturned()
    {
        // Over a share the verify reads a window of files at once; the texts it keeps for the hand-off sit outside the
        // read budget, so they get a shared cap of their own. A full cap just means the filter reads the file itself.
        using var repo = CFiles();
        var prevNet = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        long prevCap = SegmentedIndex.KeptTextCharsCap;
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            var expected = RunRefs(repo, "hot_name", new LexicalSpanFilter("hot_name"), handOffText: false);

            var shared = new LexicalSpanFilter("hot_name");
            Assert.Equal(expected, RunRefs(repo, "hot_name", shared, handOffText: true));
            Assert.Equal(0, shared.FilesReadFromDisk);
            Assert.Equal(0, SegmentedIndex.KeptTextCharsInFlight);

            SegmentedIndex.KeptTextCharsCap = 0;
            var capped = new LexicalSpanFilter("hot_name");
            Assert.Equal(expected, RunRefs(repo, "hot_name", capped, handOffText: true));
            Assert.Equal(6, capped.FilesReadFromDisk);
            Assert.Equal(0, SegmentedIndex.KeptTextCharsInFlight);
        }
        finally
        {
            SegmentedIndex.KeptTextCharsCap = prevCap;
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", prevNet);
        }
    }

    // --- SourceFile.ReadAllText == StreamReader ----------------------------------------------------------------

    private static string ViaStreamReader(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var r = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    private static string ViaSourceFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "cc-srcfile-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        try { using var fs = SourceFile.OpenSequential(path, network: false); return SourceFile.ReadAllText(fs); }
        finally { File.Delete(path); }
    }

    public static IEnumerable<object[]> Encodings()
    {
        const string s = "int héllo_wörld = 1; // ☃ \U0001F600\r\nreturn x;\n";
        yield return new object[] { "utf8 no bom", Encoding.UTF8.GetBytes(s) };
        yield return new object[] { "utf8 bom", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(s)).ToArray() };
        yield return new object[] { "utf16le bom", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(s)).ToArray() };
        yield return new object[] { "utf16be bom", Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(s)).ToArray() };
        yield return new object[] { "utf32le bom", Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(s)).ToArray() };
        var be32 = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        yield return new object[] { "utf32be bom", be32.GetPreamble().Concat(be32.GetBytes(s)).ToArray() };
        yield return new object[] { "empty", Array.Empty<byte>() };
        yield return new object[] { "bom only", new byte[] { 0xEF, 0xBB, 0xBF } };
        yield return new object[] { "invalid utf8", new byte[] { 0x61, 0xC3, 0x28, 0xFF, 0x62, 0xE2, 0x82, 0x0A, 0xF0, 0x9F } };
        yield return new object[] { "latin1", Encoding.Latin1.GetBytes("café naïve\n") };
        yield return new object[] { "utf16le odd length", new byte[] { 0xFF, 0xFE, 0x61, 0x00, 0x62 } };
        yield return new object[] { "truncated utf8 bom", new byte[] { 0xEF, 0xBB } };
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void SourceFileReadAllText_DecodesExactlyLikeStreamReader(string label, byte[] bytes)
    {
        Assert.True(ViaStreamReader(bytes) == ViaSourceFile(bytes), label);
    }

    [Fact]
    public void SourceFileReadAllText_RandomBytes_DecodeLikeStreamReader()
    {
        var rng = new Random(4242);
        for (int i = 0; i < 300; i++)
        {
            var b = new byte[rng.Next(0, 9000)];
            rng.NextBytes(b);
            if (i % 3 == 0 && b.Length > 2) { b[0] = 0xEF; b[1] = 0xBB; b[2] = 0xBF; }
            if (i % 5 == 0 && b.Length > 1) { b[0] = 0xFF; b[1] = 0xFE; }
            Assert.True(ViaStreamReader(b) == ViaSourceFile(b), $"case {i}, {b.Length} bytes");
        }
    }

    // --- fast C/C++ scanner == the old char-at-a-time scanner ---------------------------------------------------

    private static readonly string[] Pieces =
    {
        "a", "hot", "x_1", " ", "\t", "\n", "\r", "\r\n", "/", "//", "/*", "*/", "*", "\"", "'", "\\", "\\\n", "\\\r\n",
        "R\"x(", ")x\"", "R\"(", ")\"", "u8", "L", "u'", "10'000", "(", ")", ";", "é", "\U0001F600", "FOOR\"", "'a'", "'\\n'",
    };

    [Fact]
    public void CFamilySpans_MatchesTheCharAtATimeScanner_OnRandomInput()
    {
        var rng = new Random(20261007);
        for (int i = 0; i < 4000; i++)
        {
            var sb = new StringBuilder();
            int n = rng.Next(0, 120);
            for (int k = 0; k < n; k++) sb.Append(Pieces[rng.Next(Pieces.Length)]);
            var text = sb.ToString();
            var expected = CFamilyReference.Spans(text);
            var got = LexicalSpanFilter.CFamilySpans(text);
            Assert.True(expected.SequenceEqual(got),
                $"case {i}: {System.Text.Json.JsonSerializer.Serialize(text)}\n expected {string.Join(" ", expected)}\n got      {string.Join(" ", got)}");
        }
    }

    [Fact]
    public void CFamilySpans_MatchesTheCharAtATimeScanner_OnRealisticFiles()
    {
        var rng = new Random(7);
        foreach (var size in new[] { 0, 1, 1000, 200_000 })
        {
            var sb = new StringBuilder();
            while (sb.Length < size)
                sb.Append(rng.Next(6) switch
                {
                    0 => "/* block\n   comment with hot_name */\n",
                    1 => "int f(void) { return hot_name(3) / 2; } // tail hot_name\n",
                    2 => "const char *s = \"a \\\"quoted\\\" hot_name\";\r\n",
                    3 => "char c = '\\''; int n = 1'000'000;\n",
                    4 => "auto r = R\"delim(raw ) \" hot_name\n)delim\";\n",
                    _ => "#define X(a) a##_hot /* x */ * 2\n",
                });
            var text = sb.ToString();
            Assert.True(CFamilyReference.Spans(text).SequenceEqual(LexicalSpanFilter.CFamilySpans(text)), $"size {size}");
        }
    }

    // The scanner exactly as it was before it was sped up (frozen copy): the oracle for the fast one.
    private static class CFamilyReference
    {
        public static (int, int, int, int)[] Spans(string text)
        {
            var list = new List<(int, int, int, int)>();
            int n = text.Length, i = 0, line = 0, col = 0;
            while (i < n)
            {
                char c = text[i];
                char d = i + 1 < n ? text[i + 1] : '\0';
                if (c == '/' && d == '/')
                {
                    int sl = line, sc = col;
                    while (i < n && text[i] != '\n' && text[i] != '\r') { i++; col++; }
                    list.Add((sl, sc, line, col));
                    continue;
                }
                if (c == '/' && d == '*')
                {
                    int sl = line, sc = col;
                    i += 2; col += 2;
                    while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/'))
                    {
                        if (text[i] == '\n') { line++; col = 0; } else col++;
                        i++;
                    }
                    if (i < n) { i += 2; col += 2; }
                    list.Add((sl, sc, line, col));
                    continue;
                }
                if (c == '"')
                {
                    if (LexicalSpanFilter.IsRawStringOpen(text, i)) { LexicalSpanFilter.ScanRawString(text, list, ref i, ref line, ref col); continue; }
                    int sl = line, sc = col, j = i + 1, jl = line, jc = col + 1;
                    bool closed = false;
                    while (j < n)
                    {
                        char t = text[j];
                        if (t == '\\' && j + 1 < n)
                        {
                            if (text[j + 1] == '\n') { j += 2; jl++; jc = 0; continue; }
                            if (text[j + 1] == '\r' && j + 2 < n && text[j + 2] == '\n') { j += 3; jl++; jc = 0; continue; }
                            j += 2; jc += 2; continue;
                        }
                        if (t == '"') { closed = true; j++; jc++; break; }
                        if (t == '\n' || t == '\r') break;
                        j++; jc++;
                    }
                    if (closed) { list.Add((sl, sc, jl, jc)); i = j; line = jl; col = jc; }
                    else { i++; col++; }
                    continue;
                }
                if (c == '\'')
                {
                    if (i > 0 && LexicalSpanFilter.IsIdentifierChar(text[i - 1]) && !LexicalSpanFilter.HasEncodingPrefix(text, i)) { i++; col++; continue; }
                    int sl = line, sc = col, j = i + 1, jcol = col + 1, scanned = 0;
                    bool closed = false;
                    while (j < n && text[j] != '\n' && text[j] != '\r' && scanned < 16)
                    {
                        if (text[j] == '\\' && j + 1 < n) { j += 2; jcol += 2; scanned += 2; continue; }
                        if (text[j] == '\'') { closed = true; j++; jcol++; break; }
                        j++; jcol++; scanned++;
                    }
                    if (closed) { list.Add((sl, sc, line, jcol)); i = j; col = jcol; }
                    else { i++; col++; }
                    continue;
                }
                if (c == '\n') { line++; col = 0; i++; continue; }
                i++; col++;
            }
            return list.ToArray();
        }
    }
}
