namespace CodeCompass.Core.Indexing.Segments;

/// <summary>LEB128 unsigned varint encode/decode used for delta-compressed posting lists.</summary>
internal static class Varint
{
    public static void Write(Stream s, uint value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    /// <summary>Decode from a byte span starting at offset; advances offset past the value.</summary>
    public static uint Read(ReadOnlySpan<byte> data, ref int offset)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }
}
