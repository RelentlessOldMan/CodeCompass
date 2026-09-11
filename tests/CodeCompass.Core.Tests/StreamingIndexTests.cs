using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

public class StreamingIndexTests
{
    // The streamed trigram set + content hash must be byte-for-byte identical to the whole-file path,
    // or a file that crosses the streaming threshold would index differently than a small one.
    [Theory]
    [InlineData("class Foo { void Bar() { int x = 42; } }\nprivate readonly Baz qux;\n")]
    [InlineData("línea uno\ncafé + naïve — résumé\nмного текста\n日本語のテキスト\n")] // multi-byte UTF-8
    [InlineData("")]
    [InlineData("ab")] // shorter than a trigram
    public void StreamedTrigramsAndHash_MatchWholeFile(string content)
    {
        using var repo = new TempRepo();
        var full = repo.WriteBytes("f.txt", Encoding.UTF8.GetBytes(content));

        var bytes = System.IO.File.ReadAllBytes(full);
        var expectedTrigrams = TrigramIndex.ComputeTrigrams(TextDecoder.FromBytes(bytes)).OrderBy(x => x).ToArray();
        var expectedHash = ContentHasher.Hash(bytes);

        Assert.True(LargeFileIndexer.TryStreamIndex(full, out var got, out var len, out var hash, out var bin));
        Assert.False(bin);
        Assert.Equal(bytes.Length, len);
        Assert.Equal(expectedHash, hash);
        Assert.Equal(expectedTrigrams, got.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void StreamedTrigrams_MatchWholeFile_AcrossChunkBoundaries()
    {
        // >1 MB so the 1 MB read chunk boundary is crossed several times: exercises the decoder state
        // and the 2-char trigram carry between chunks.
        var sb = new StringBuilder(1_600_000);
        int i = 0;
        while (sb.Length < 1_500_000)
            sb.Append("void method_").Append(i++).Append("(int a, char* b) { return a + 0x").Append((i * 7).ToString("X")).Append("; }\n");
        var content = sb.ToString();

        using var repo = new TempRepo();
        var full = repo.WriteBytes("big.txt", Encoding.UTF8.GetBytes(content));
        var bytes = System.IO.File.ReadAllBytes(full);

        var expected = TrigramIndex.ComputeTrigrams(TextDecoder.FromBytes(bytes)).OrderBy(x => x).ToArray();
        Assert.True(LargeFileIndexer.TryStreamIndex(full, out var got, out var len, out var hash, out _));
        Assert.Equal(bytes.Length, len);
        Assert.Equal(ContentHasher.Hash(bytes), hash);
        Assert.Equal(expected, got.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void StreamedTrigramsAndHash_MatchWholeFile_WithUtf8Bom()
    {
        var text = "namespace N { class WithBom { } }\n";
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        using var repo = new TempRepo();
        var full = repo.WriteBytes("bom.txt", withBom);
        var bytes = System.IO.File.ReadAllBytes(full);

        // TextDecoder strips the BOM; streaming must too (trigrams), while the hash covers raw bytes.
        var expected = TrigramIndex.ComputeTrigrams(TextDecoder.FromBytes(bytes)).OrderBy(x => x).ToArray();
        Assert.True(LargeFileIndexer.TryStreamIndex(full, out var got, out _, out var hash, out _));
        Assert.Equal(ContentHasher.Hash(bytes), hash);
        Assert.Equal(expected, got.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void StreamIndex_DetectsBinary()
    {
        using var repo = new TempRepo();
        var full = repo.WriteBytes("data.bin", new byte[] { 1, 2, 0, 3, 0, 4, 5, 0, 6 });
        Assert.False(LargeFileIndexer.TryStreamIndex(full, out _, out _, out _, out var bin));
        Assert.True(bin);
    }

    // End-to-end: a file above the streaming threshold (~128 MB) is indexed by Build via the streaming
    // path and is then text-searchable via the streaming query scan. Larger than the ~1 GB decode
    // ceiling would be nicer to prove but slow; >128 MB is enough to exercise both streamed paths.
    [Fact]
    public void EndToEnd_LargeFileAboveThreshold_IndexedAndSearchable()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("huge.txt");
        const string marker = "UNIQUEMARKER_XYZZY_42";
        using (var w = new System.IO.StreamWriter(path, append: false, Encoding.UTF8))
        {
            long written = 0;
            int i = 0;
            while (written < 130L * 1024 * 1024)
            {
                var line = $"line {i} some ordinary code-like content foo bar baz\n";
                w.Write(line);
                written += line.Length;
                if (i == 400_000) { w.Write(marker + "\n"); written += marker.Length + 1; }
                i++;
            }
        }

        var (text, symbols, _) = RepositoryIndexer.Build(repo.Root);
        using (text)
        using (symbols)
        {
            var matches = text.Search(marker);
            Assert.Contains(matches, m => m.LineText.Contains(marker));
        }
    }
}

public class FileScannerTests
{
    // The streaming line scan must match the whole-text scan for ordinary (single-line) queries.
    [Theory]
    [InlineData("alpha beta\nbeta gamma beta\ndelta\n", "beta")]
    [InlineData("one two\r\nthree two two\r\nfour\r\n", "two")] // CRLF
    [InlineData("no match here\nnor here\n", "zzz")]
    public void ScanByLine_MatchesScanText(string content, string query)
    {
        using var repo = new TempRepo();
        var full = repo.WriteBytes("f.txt", Encoding.UTF8.GetBytes(content));

        var whole = new List<SearchMatch>();
        FileScanner.ScanText("f.txt", TextDecoder.FromBytes(System.IO.File.ReadAllBytes(full)), query, whole, 1000);

        var streamed = new List<SearchMatch>();
        FileScanner.ScanByLine("f.txt", full, query, streamed, 1000);

        Assert.Equal(whole.Count, streamed.Count);
        for (int i = 0; i < whole.Count; i++)
        {
            Assert.Equal(whole[i].Line, streamed[i].Line);
            Assert.Equal(whole[i].Column, streamed[i].Column);
            Assert.Equal(whole[i].LineText, streamed[i].LineText);
        }
    }
}
