using System.IO;
using System.Text;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// A positional sidecar is TRUSTED data read straight into array allocations and a Bloom-bit modulus. A
// corrupt-but-plausible header must be rejected (Get returns null -> whole-file fallback), never fed
// through: a bad bloomK loops MayContain forever, a truncated bloom silently shifts the bit modulus and
// causes FALSE NEGATIVES (a real match tests negative and the block is skipped), a garbage count forces
// multi-GB allocations. These pin each guard in SidecarCache.Parse. Fast, deterministic, native-free.
public class SidecarParseTests
{
    private const int Magic = 0x43435031; // "CCP1"

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-sidecar-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // Writes a sidecar file. Knobs let each test corrupt exactly one field while the rest stays well-formed.
    private static string WriteSidecar(string dir, string rel, int magic = Magic, int bloomBytes = 8,
        int bloomK = 4, int bomLen = 0, int count = 1, long startByte = 0, long endByte = 10,
        int? bloomLenOverride = null)
    {
        var path = Path.Combine(dir, "f.pos");
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(magic);
            w.Write(rel);
            w.Write(bloomBytes);
            w.Write(bloomK);
            w.Write(bomLen);
            w.Write(count);
            for (int i = 0; i < count; i++) { w.Write(1); w.Write(startByte); w.Write(endByte); } // line, start, end
            for (int i = 0; i < count; i++) w.Write(new byte[bloomLenOverride ?? bloomBytes]);
        }
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public void Get_WellFormedSidecar_Parses()
    {
        var dir = NewDir();
        try
        {
            SidecarCache.Clear();
            var p = WriteSidecar(dir, "src/big.cs");
            var sc = SidecarCache.Get(p, "src/big.cs", out var bytesRead);
            Assert.NotNull(sc);
            Assert.Equal(1, sc!.Count);
            Assert.True(bytesRead > 0); // a miss reports the file bytes read
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Get_BadMagic_ReturnsNull()
    {
        var dir = NewDir();
        try
        {
            SidecarCache.Clear();
            var p = WriteSidecar(dir, "src/big.cs", magic: 0x12345678); // not "CCP1"
            Assert.Null(SidecarCache.Get(p, "src/big.cs", out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Get_WrongRel_ReturnsNull()
    {
        // A sidecar built for one path must not be served for another (hash-collision guard).
        var dir = NewDir();
        try
        {
            SidecarCache.Clear();
            var p = WriteSidecar(dir, "src/big.cs");
            Assert.Null(SidecarCache.Get(p, "src/OTHER.cs", out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData(0)]   // bloomK < 1 : MayContain would loop / divide-by-zero
    [InlineData(65)]  // bloomK > 64 : out of the sane range
    public void Get_BadBloomK_ReturnsNull(int bloomK)
    {
        var dir = NewDir();
        try
        {
            SidecarCache.Clear();
            var p = WriteSidecar(dir, "src/big.cs", bloomK: bloomK);
            Assert.Null(SidecarCache.Get(p, "src/big.cs", out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Get_BomLenOutOfRange_ReturnsNull()
    {
        var dir = NewDir();
        try
        {
            SidecarCache.Clear();
            var p = WriteSidecar(dir, "src/big.cs", bomLen: 5); // valid range is 0..4
            Assert.Null(SidecarCache.Get(p, "src/big.cs", out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Get_CountExceedsFileBytes_ReturnsNull_NoHugeAlloc()
    {
        // A garbage/truncated count must be bounded against the bytes actually remaining BEFORE allocating
        // count-sized arrays, or it forces a multi-GB allocation -> OOM.
        var dir = NewDir();
        try
        {
            SidecarCache.Clear();
            // A file that CLAIMS a huge block count but holds a tiny body: count * (20 + bloomBytes) far
            // exceeds the bytes remaining, so it must be rejected before allocating count-sized arrays.
            var bad = WriteSidecarRawHugeCount(dir, "src/big.cs");
            Assert.Null(SidecarCache.Get(bad, "src/big.cs", out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Get_InvertedBlockRange_ReturnsNull()
    {
        var dir = NewDir();
        try
        {
            SidecarCache.Clear();
            var p = WriteSidecar(dir, "src/big.cs", startByte: 100, endByte: 10); // end < start
            Assert.Null(SidecarCache.Get(p, "src/big.cs", out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Get_TruncatedBloom_ReturnsNull()
    {
        // The false-negative guard: a bloom shorter than bloomBytes changes the bit modulus and would make a
        // real match test negative. Must be rejected, not silently accepted.
        var dir = NewDir();
        try
        {
            SidecarCache.Clear();
            var p = WriteSidecar(dir, "src/big.cs", bloomBytes: 8, bloomLenOverride: 4); // declares 8, writes 4
            Assert.Null(SidecarCache.Get(p, "src/big.cs", out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string WriteSidecarRawHugeCount(string dir, string rel)
    {
        var path = Path.Combine(dir, "huge.pos");
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(rel);
            w.Write(8);          // bloomBytes
            w.Write(4);          // bloomK
            w.Write(0);          // bomLen
            w.Write(1_000_000);  // count: claims a million blocks...
            // ...but the body holds only one partial block, far short of count * (20 + bloomBytes).
            w.Write(1); w.Write(0L); w.Write(10L);
        }
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }
}
