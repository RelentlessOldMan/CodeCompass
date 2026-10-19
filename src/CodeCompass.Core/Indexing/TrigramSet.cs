using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CodeCompass.Core.Indexing;

/// <summary>
/// A reusable distinct-trigram collector for the build's per-char hot loop. A trigram of three ASCII chars (nearly all of
/// source text) is deduped by one bit in a 2^21-bit table indexed by its 7-bit chars, and listed once on first sight;
/// anything else falls back to a <see cref="HashSet{T}"/>. The general hash set it replaces paid a full hash probe per
/// char, and most of those probes were repeats. <see cref="Drain"/> clears only the bits it set, so reuse costs
/// O(distinct), never the whole table. NOT thread-safe: one per worker thread.
/// </summary>
public sealed class TrigramSet
{
    private const int AsciiBits = 1 << 21;
    private readonly ulong[] _seen = new ulong[AsciiBits / 64]; // 256 KB
    private int[] _ascii = new int[InitialAscii];               // table indexes, in first-seen order
    private int _asciiCount;
    private readonly HashSet<long> _other = new();
    private const int InitialAscii = 4096;

    public int Count => _asciiCount + _other.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(char a, char b, char c)
    {
        if ((a | b | c) < 128)
        {
            int idx = (a << 14) | (b << 7) | c;
            ref ulong word = ref _seen[idx >> 6];
            ulong bit = 1UL << idx;
            if ((word & bit) != 0) return;
            word |= bit;
            if (_asciiCount == _ascii.Length) Array.Resize(ref _ascii, _ascii.Length * 2);
            _ascii[_asciiCount++] = idx;
        }
        else _other.Add(TrigramIndex.TriKey(a, b, c));
    }

    /// <summary>Every distinct trigram added since the last drain (in no particular order), leaving the set empty. Past
    /// <paramref name="trimAbove"/> distinct trigrams the buffers are shrunk, so one outlier file doesn't pin them.</summary>
    public long[] Drain(int trimAbove)
    {
        int total = Count;
        var result = total == 0 ? Array.Empty<long>() : new long[total];
        for (int i = 0; i < _asciiCount; i++)
        {
            int idx = _ascii[i];
            _seen[idx >> 6] &= ~(1UL << idx);
            result[i] = TrigramIndex.TriKey((char)(idx >> 14), (char)((idx >> 7) & 127), (char)(idx & 127));
        }
        _other.CopyTo(result, _asciiCount);
        _asciiCount = 0;
        _other.Clear();
        if (total > trimAbove)
        {
            if (_ascii.Length > InitialAscii) _ascii = new int[InitialAscii];
            _other.TrimExcess();
        }
        return result;
    }

    /// <summary>Capacity the scratch buffers hold between files (for the trim regression test).</summary>
    internal int RetainedCapacity => _ascii.Length + _other.EnsureCapacity(0);
}
