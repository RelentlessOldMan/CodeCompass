namespace CodeCompass.Core.Indexing;

/// <summary>
/// A fixed-size Bloom filter over trigram keys (longs), used per block of a large streamed file so a
/// search can skip blocks that cannot contain the query. No false negatives (a block that truly holds
/// a trigram always tests positive), so it never hides a real match; false positives just cost an
/// extra block read, which the scan then rules out. Backed by a caller-owned byte buffer so it can be
/// written to / read from a sidecar file directly.
/// </summary>
public sealed class BloomFilter
{
    private readonly byte[] _bits;
    private readonly int _bitCount;
    private readonly int _mask; // _bitCount - 1 when it's a power of two (every sidecar's 16 KB is), else -1
    private readonly int _k;

    public BloomFilter(byte[] bits, int k)
    {
        _bits = bits;
        _bitCount = bits.Length * 8;
        _mask = _bitCount > 0 && (_bitCount & (_bitCount - 1)) == 0 ? _bitCount - 1 : -1;
        _k = k;
    }

    public static BloomFilter Create(int bytes, int k) => new(new byte[bytes], k);

    public byte[] Bits => _bits;

    public void Add(long key)
    {
        var (h, h2) = Hashes(key);
        for (int i = 0; i < _k; i++, h += h2)
        {
            int bit = Bit(h);
            _bits[bit >> 3] |= (byte)(1 << (bit & 7));
        }
    }

    public bool MayContain(long key)
    {
        var (h, h2) = Hashes(key);
        for (int i = 0; i < _k; i++, h += h2)
        {
            int bit = Bit(h);
            if ((_bits[bit >> 3] & (byte)(1 << (bit & 7))) == 0) return false;
        }
        return true;
    }

    // The i-th probe h1 + i*h2 (stepped, wrapping as the product does) mod the bit count. For a power of two that's a mask -
    // the same bit, without a 64-bit divide per probe (four per trigram of every big file).
    private int Bit(long h)
    {
        h &= long.MaxValue;
        return _mask >= 0 ? (int)(h & _mask) : (int)(h % _bitCount);
    }

    // Two independent-ish hashes from one 64-bit key (a splitmix-style mix), combined via double
    // hashing to synthesize k hash functions.
    private static (long H1, long H2) Hashes(long key)
    {
        ulong x = (ulong)key;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        ulong y = (ulong)key + 0x9E3779B97F4A7C15UL;
        y = (y ^ (y >> 29)) * 0xBF58476D1CE4E5B9UL;
        y ^= y >> 32;
        return ((long)(x & long.MaxValue), (long)((y | 1) & long.MaxValue)); // h2 odd & positive
    }
}
