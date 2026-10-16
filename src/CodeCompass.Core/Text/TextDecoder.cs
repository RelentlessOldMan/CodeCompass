using System.Text;

namespace CodeCompass.Core.Text;

/// <summary>
/// Decodes file bytes to text the SAME way File.ReadAllText does - detecting and
/// stripping a byte-order mark, honoring UTF-8/UTF-16/UTF-32 BOMs. Indexing and search
/// must use this identically, or a BOM shifts reported line/column positions.
/// </summary>
public static class TextDecoder
{
    public static string FromBytes(byte[] bytes) => FromBytes(bytes.AsSpan());

    /// <summary>StreamReader's detectEncodingFromByteOrderMarks applied to the whole buffer, decoded in one pass.
    /// StreamReader.ReadToEnd decodes into a growing StringBuilder and then copies it into the final string - two text
    /// copies (~4x the bytes for ASCII) where this allocates only the string. Same BOM rules, same UTF-8 default, same
    /// U+FFFD for invalid bytes.</summary>
    public static string FromBytes(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b[2..]);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return b.Length >= 4 && b[2] == 0 && b[3] == 0 ? Encoding.UTF32.GetString(b[4..]) : Encoding.Unicode.GetString(b[2..]);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b[3..]);
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(b[4..]);
        return Encoding.UTF8.GetString(b);
    }
}
