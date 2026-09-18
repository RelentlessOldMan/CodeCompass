using System.IO;
using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

public class EncodingIndexTests
{
    private static void WriteBytes(TempRepo repo, string rel, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(repo.Root, rel.Replace('/', Path.DirectorySeparatorChar)), bytes);

    [Theory]
    [InlineData("utf16le.cmm")]
    [InlineData("utf16be.txt")]
    public void Utf16File_WithBom_IsIndexedAndTextSearchable(string rel)
    {
        // A BOM-marked UTF-16 file (e.g. a Windows-written TRACE32 .cmm or a UTF-16 log) must be indexed
        // for text search, not skipped as "binary" because its ASCII chars carry NUL bytes.
        var enc = rel.Contains("be") ? Encoding.BigEndianUnicode : Encoding.Unicode;
        using var repo = new TempRepo();
        repo.Write("keep.cs", "class Keep { }"); // a normal file so the index isn't trivially empty
        WriteBytes(repo, rel, enc.GetPreamble().Concat(enc.GetBytes("marker UniqueUtf16Token here\n")).ToArray());

        var (text, symbols, _) = RepositoryIndexer.Build(repo.Root);
        using (text) using (symbols)
        {
            // The UTF-16 content is decoded and searchable...
            var hits = text.Search("UniqueUtf16Token", 10);
            Assert.NotEmpty(hits);
            Assert.Equal(rel, hits[0].Path);
            Assert.Equal(1, hits[0].Line); // BOM stripped -> the token is on line 1, not shifted
            // ...and the normal file is unaffected.
            Assert.NotEmpty(text.Search("Keep", 10));
        }
    }
}
