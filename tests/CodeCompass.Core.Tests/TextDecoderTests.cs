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
        }
        finally { File.Delete(tmp); }
    }

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
