using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Compares or searches a UTF-8 string stored in a mapped view without allocating it. The bytes are decoded to UTF-16
/// into a stack (or pooled) buffer, the same chars <see cref="Encoding.GetString(byte[])"/> would give, so every answer
/// matches the string-based one: a binary search over a sorted column made a byte[] and a string per probe, and a name
/// scan made two per name.
/// </summary>
internal static class MappedUtf8
{
    private const int StackChars = 256;

    /// <summary><see cref="string.CompareOrdinal(string, string)"/>'s sign for the stored string against
    /// <paramref name="key"/>. UTF-16 order, as the columns are sorted - not UTF-8 byte order, which differs for
    /// characters past U+FFFF.</summary>
    public static int CompareOrdinal(MemoryMappedViewAccessor view, long offset, int len, ReadOnlySpan<char> key)
    {
        if (len == 0) return key.IsEmpty ? 0 : -1;
        char[]? rented = null;
        Span<char> chars = len <= StackChars ? stackalloc char[StackChars] : (rented = ArrayPool<char>.Shared.Rent(len));
        try { return Math.Sign(((ReadOnlySpan<char>)chars[..Decode(view, offset, len, chars)]).SequenceCompareTo(key)); }
        finally { if (rented is not null) ArrayPool<char>.Shared.Return(rented); }
    }

    /// <summary>Does the stored string contain <paramref name="value"/> under <paramref name="comparison"/>?</summary>
    public static bool Contains(MemoryMappedViewAccessor view, long offset, int len, ReadOnlySpan<char> value, StringComparison comparison)
    {
        if (len == 0) return value.IsEmpty;
        char[]? rented = null;
        Span<char> chars = len <= StackChars ? stackalloc char[StackChars] : (rented = ArrayPool<char>.Shared.Rent(len));
        try { return ((ReadOnlySpan<char>)chars[..Decode(view, offset, len, chars)]).Contains(value, comparison); }
        finally { if (rented is not null) ArrayPool<char>.Shared.Return(rented); }
    }

    // UTF-8 never decodes to more UTF-16 chars than it has bytes (an invalid byte becomes one U+FFFD), so `chars` of
    // `len` always fits.
    private static int Decode(MemoryMappedViewAccessor view, long offset, int len, Span<char> chars)
    {
        byte[]? rented = null;
        Span<byte> bytes = len <= StackChars ? stackalloc byte[StackChars] : (rented = ArrayPool<byte>.Shared.Rent(len));
        try
        {
            bytes = bytes[..len];
            view.SafeMemoryMappedViewHandle.ReadSpan((ulong)(view.PointerOffset + offset), bytes);
            return Encoding.UTF8.GetChars(bytes, chars);
        }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
    }
}
