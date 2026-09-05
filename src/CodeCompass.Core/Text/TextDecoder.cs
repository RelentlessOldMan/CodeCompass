using System.Text;

namespace CodeCompass.Core.Text;

/// <summary>
/// Decodes file bytes to text the SAME way File.ReadAllText does - detecting and
/// stripping a byte-order mark, honoring UTF-8/UTF-16/UTF-32 BOMs. Indexing and search
/// must use this identically, or a BOM shifts reported line/column positions.
/// </summary>
public static class TextDecoder
{
    public static string FromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
