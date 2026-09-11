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
    private readonly int _k;

    public BloomFilter(byte[] bits, int k)
    {
        _bits = bits;
        _bitCount = bits.Length * 8;
        _k = k;
    }

    public static BloomFilter Create(int bytes, int k) => new(new byte[bytes], k);

    public byte[] Bits => _bits;

    public void Add(long key)
    {
        var (h1, h2) = Hashes(key);
        for (int i = 0; i < _k; i++)
        {
            int bit = (int)(((h1 + (long)i * h2) & long.MaxValue) % _bitCount);
            _bits[bit >> 3] |= (byte)(1 << (bit & 7));
        }
    }

    public bool MayContain(long key)
    {
        var (h1, h2) = Hashes(key);
        for (int i = 0; i < _k; i++)
        {
            int bit = (int)(((h1 + (long)i * h2) & long.MaxValue) % _bitCount);
            if ((_bits[bit >> 3] & (byte)(1 << (bit & 7))) == 0) return false;
        }
        return true;
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
