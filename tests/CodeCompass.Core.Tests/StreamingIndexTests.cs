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

    // TryStreamIndex builds a sound block/positional index: every block's Bloom admits every trigram
    // in that block's own byte range (no false negatives -> a search can never miss a real match),
    // and the block table is contiguous with monotonic line numbers.
    [Fact]
    public void StreamIndex_Blocks_AreSoundAndContiguous()
    {
        var sb = new StringBuilder(3_000_000);
        int i = 0;
        while (sb.Length < 2_600_000) // > 2 blocks at ~1 MB each
            sb.Append("HEY_MOM_MY_CHIP_REG_").Append(i++).Append(" = 0x").Append((i * 3).ToString("X")).Append('\n');

        using var repo = new TempRepo();
        var full = repo.WriteBytes("regs.h", Encoding.UTF8.GetBytes(sb.ToString()));

        Assert.True(LargeFileIndexer.TryStreamIndex(full, out _, out var len, out _, out _, out var blocks));
        Assert.NotNull(blocks);
        Assert.True(blocks!.Blocks.Count >= 2);

        var bytes = System.IO.File.ReadAllBytes(full);
        long expectedByte = 0;
        int expectedLine = 1;
        foreach (var b in blocks.Blocks)
        {
            Assert.Equal(expectedByte, b.StartByte);       // contiguous, no gaps/overlaps
            Assert.Equal(expectedLine, b.StartLine);       // monotonic line numbers
            var blockText = Encoding.UTF8.GetString(bytes, (int)b.StartByte, (int)(b.EndByte - b.StartByte));
            var bloom = new BloomFilter(b.Bloom, LargeFileIndexer.BloomK);
            foreach (var tri in TrigramIndex.ComputeTrigrams(blockText))
                Assert.True(bloom.MayContain(tri)); // soundness: no false negatives
            expectedByte = b.EndByte;
            expectedLine += blockText.Count(c => c == '\n');
        }
        Assert.Equal(len, expectedByte); // blocks cover the whole file
    }

    // End-to-end through the positional sidecar: a large file (>128 MB) is indexed by Build (which
    // writes the sidecar), and a search for a marker that appears exactly once - well past the first
    // block - finds it at the correct line via block lookup, without reading the whole file.
    [Fact]
    public void EndToEnd_LargeFile_SearchFindsMarkerAtCorrectLineViaSidecar()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("huge.h");
        const string marker = "ZZ_UNIQUE_MARKER_98765";
        int markerLine = -1;
        using (var w = new System.IO.StreamWriter(path, append: false, Encoding.UTF8))
        {
            long written = 0;
            int line = 0;
            while (written < 140L * 1024 * 1024)
            {
                line++;
                string lineText = line == 900_000 ? marker : $"HEY_MOM_MY_CHIP_REG_{line} = 0x{line:X}";
                if (line == 900_000) markerLine = line;
                w.Write(lineText); w.Write('\n');
                written += lineText.Length + 1;
            }
        }

        var (text, symbols, _) = RepositoryIndexer.Build(repo.Root);
        using (text)
        using (symbols)
        {
            var matches = text.Search(marker);
            var hit = Assert.Single(matches);
            Assert.Equal("huge.h", hit.Path);
            Assert.Equal(markerLine, hit.Line);       // correct line via the block's start-line offset
            Assert.Equal(marker, hit.LineText);
            Assert.Equal(1, hit.Column);
        }
    }
}

public class BloomFilterTests
{
    [Fact]
    public void NoFalseNegatives()
    {
        var b = BloomFilter.Create(4096, 4);
        var keys = Enumerable.Range(0, 2000).Select(i => (long)i * 2654435761L).ToArray();
        foreach (var k in keys) b.Add(k);
        foreach (var k in keys) Assert.True(b.MayContain(k)); // every added key must test positive
    }

    [Fact]
    public void RoundTripsThroughBits()
    {
        var b = BloomFilter.Create(1024, 3);
        b.Add(12345); b.Add(67890);
        var reopened = new BloomFilter(b.Bits, 3); // same backing bytes (as stored in a sidecar)
        Assert.True(reopened.MayContain(12345));
        Assert.True(reopened.MayContain(67890));
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
