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
