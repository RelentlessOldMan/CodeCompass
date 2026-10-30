using System;
using System.IO;
using System.Text;
using CodeCompass.Core.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// TextDecoder must decode bytes byte-for-byte the same way File.ReadAllText does, because
// indexing decodes via TextDecoder while search re-reads matched files via File.ReadAllText -
// any divergence shifts reported line/column numbers.
public class TextDecoderTests
{
    private static void AssertMatchesReadAllText(byte[] bytes)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cc-dec-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(tmp, bytes);
            Assert.Equal(File.ReadAllText(tmp), TextDecoder.FromBytes(bytes));
            AssertDecodeMatchesFromBytes(bytes);
        }
        finally { File.Delete(tmp); }
    }

    // The build decodes into a buffer of exactly one char per byte (TextDecoder.Decode): it must fit, and give FromBytes's text.
    private static void AssertDecodeMatchesFromBytes(byte[] bytes)
    {
        var dest = new char[bytes.Length];
        int n = TextDecoder.Decode(bytes, dest);
        Assert.Equal(TextDecoder.FromBytes(bytes), new string(dest, 0, n));
    }

    // The most chars per byte each encoding can give: invalid bytes (one U+FFFD each), a UTF-16 odd trailing byte, a
    // truncated UTF-32 unit, and astral UTF-32 (two chars per four bytes) - none may outgrow a one-char-per-byte buffer.
    [Theory]
    [InlineData(new byte[] { 0x80, 0x81, 0xC0, 0xC1, 0xF5, 0xFE, 0x80 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00, 0x42 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0xD8, 0x00, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x00, 0xF6, 0x01, 0x00, 0x41 })]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x01, 0xF6, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xF0, 0x9F, 0x98 })]
    [InlineData(new byte[0])]
    public void Decode_WorstCaseBytes_FitsOneCharPerByte_AndMatchesFromBytes(byte[] bytes) => AssertDecodeMatchesFromBytes(bytes);

    [Fact]
    public void Utf8Bom_IsStripped_AndMatchesReadAllText()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("class Foo"));
        var s = TextDecoder.FromBytes(bytes);
        Assert.Equal("class Foo", s);
        Assert.False(s.StartsWith('﻿'));
        AssertMatchesReadAllText(bytes);
    }

    [Fact]
    public void Utf16LE_WithBom_Decodes()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("héllo\nworld"));
        Assert.Equal("héllo\nworld", TextDecoder.FromBytes(bytes));
        AssertMatchesReadAllText(bytes);
    }

    [Fact]
    public void Utf16BE_WithBom_Decodes()
    {
        var bytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("café\nx"));
        Assert.Equal("café\nx", TextDecoder.FromBytes(bytes));
        AssertMatchesReadAllText(bytes);
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        Assert.Equal("", TextDecoder.FromBytes(Array.Empty<byte>()));
    }

    [Fact]
    public void InvalidUtf8_DoesNotThrow_AndMatchesReadAllText()
    {
        var bytes = new byte[] { 0x41, 0xC3, 0x28, 0x42 }; // 'A', bad 2-byte seq, 'B'
        var ex = Record.Exception(() => TextDecoder.FromBytes(bytes));
        Assert.Null(ex);
        AssertMatchesReadAllText(bytes);
    }

    [Fact]
    public void Utf32_WithBom_BothEndians_MatchReadAllText()
    {
        AssertMatchesReadAllText(new UTF32Encoding(false, true).GetPreamble().Concat(new UTF32Encoding(false, false).GetBytes("a€😀\nz")));
        AssertMatchesReadAllText(new UTF32Encoding(true, true).GetPreamble().Concat(new UTF32Encoding(true, false).GetBytes("a€😀\nz")));
    }

    [Fact]
    public void LargeText_WithInvalidSequencesAcrossBufferBoundaries_MatchesReadAllText()
    {
        // StreamReader decodes in buffer-sized chunks; a one-pass decode must still agree where an invalid or
        // multibyte sequence straddles a chunk edge.
        var rng = new Random(7);
        var bytes = new byte[3 * 1024 * 1024 + 17];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (i % 97) switch { 0 => 0xE2, 1 => 0x82, 2 => 0xAC, 50 => (byte)(0x80 + rng.Next(64)), 51 => 0xC3, _ => (byte)('a' + rng.Next(26)) };
        AssertMatchesReadAllText(bytes);
    }

    [Fact]
    public void FromBytes_AllocatesAboutOneCopyOfTheText()
    {
        // The build's read budget reserves 3x a file's size (raw bytes + decode). StreamReader.ReadToEnd decoded into a
        // growing StringBuilder and then copied it into the final string - about two text copies (~4x the bytes for
        // ASCII) on top of the raw bytes, past the reservation. A one-pass decode allocates just the string (~2x).
        var bytes = Encoding.ASCII.GetBytes(new string('x', 4 * 1024 * 1024));
        TextDecoder.FromBytes(bytes); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        var s = TextDecoder.FromBytes(bytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(bytes.Length, s.Length);
        Assert.True(allocated < 2.5 * bytes.Length, $"allocated {allocated:N0} bytes to decode {bytes.Length:N0}");
    }
}

internal static class ByteConcatExtensions
{
    public static byte[] Concat(this byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
