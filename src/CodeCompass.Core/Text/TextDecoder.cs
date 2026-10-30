using System.Text;

namespace CodeCompass.Core.Text;

/// <summary>
/// Decodes file bytes to text the SAME way File.ReadAllText does - detecting and
/// stripping a byte-order mark, honoring UTF-8/UTF-16/UTF-32 BOMs. Indexing and search
/// must use this identically, or a BOM shifts reported line/column positions.
/// </summary>
public static class TextDecoder
{
    private static readonly Encoding Utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: false);

    public static string FromBytes(byte[] bytes) => FromBytes(bytes.AsSpan());

    /// <summary>StreamReader's detectEncodingFromByteOrderMarks applied to the whole buffer, decoded in one pass.
    /// StreamReader.ReadToEnd decodes into a growing StringBuilder and then copies it into the final string - two text
    /// copies (~4x the bytes for ASCII) where this allocates only the string. Same BOM rules, same UTF-8 default, same
    /// U+FFFD for invalid bytes.</summary>
    public static string FromBytes(ReadOnlySpan<byte> b)
    {
        var (enc, bom) = Detect(b);
        return enc.GetString(b[bom..]);
    }

    /// <summary>As <see cref="FromBytes(ReadOnlySpan{byte})"/>, but into <paramref name="dest"/>, which must hold at
    /// least <c>b.Length</c> chars (no encoding here decodes to more chars than it has bytes: a UTF-8 sequence or an
    /// invalid byte gives at most one char per byte, UTF-16 one per two, UTF-32 at most two per four). Returns the
    /// chars written - the same chars FromBytes returns.</summary>
    public static int Decode(ReadOnlySpan<byte> b, Span<char> dest)
    {
        var (enc, bom) = Detect(b);
        return enc.GetChars(b[bom..], dest);
    }

    private static (Encoding Encoding, int BomLength) Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode, 2);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return b.Length >= 4 && b[2] == 0 && b[3] == 0 ? (Encoding.UTF32, 4) : (Encoding.Unicode, 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8, 3);
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) return (Utf32BigEndian, 4);
        return (Encoding.UTF8, 0);
    }
}
