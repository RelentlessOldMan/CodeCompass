using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Changes.Segments;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Hash ledger format v2 (docs/hash-ledger-format.md), the layout CodeDiffer reads: v1's five columns plus change times,
// file ids, hashed-at (our clock) and SHA-256 (all zero - CodeCompass doesn't compute it). CodeDiffer ignores v1 ledgers
// because without the change time it can't rule out a same-size rewrite whose modified time was put back.
public class LedgerV2Tests
{
    private static readonly FileState Sample = new(123, 638_000_000_000_000_000, "00112233445566778899AABBCCDDEEFF",
        ChangeTicks: 638_000_000_000_000_007, FileId: 0x0001_0000_0000_002A, HashedAtTicks: 638_000_000_100_000_000);

    [Fact]
    public void Base_V2_RoundTripsEveryColumn()
    {
        using var tmp = new TempRepo();
        var path = Path.Combine(tmp.Root, "snapshot-00000000.base");
        var pending = Sample with { MTimeTicks = -Sample.MTimeTicks };
        SnapshotBaseFile.Write(path, 2, 2, new[] { ("a", Sample), ("b", pending) });
        using var r = new SnapshotBaseReader(path);
        Assert.Equal(Sample, r.GetState(0));
        Assert.Equal(pending, r.GetState(1));
    }

    // Decoded with an independent reader that follows the spec's byte layout (the same layout CodeDiffer's
    // LedgerFormat.ReadBase uses), so the writer can't drift from what the other tool expects.
    [Fact]
    public void Base_V2_MatchesTheSpecLayout()
    {
        using var tmp = new TempRepo();
        var path = Path.Combine(tmp.Root, "snapshot-00000000.base");
        SnapshotBaseFile.Write(path, 1, 5, new[] { ("dir/x", Sample) });
        var s = File.ReadAllBytes(path);

        Assert.Equal(0x4E535343u, BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan()));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(8)));
        var off = Enumerable.Range(0, 9).Select(i => BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(12 + 8 * i))).ToArray();
        long[] widths = { 8, 0, 8, 8, 16, 8, 8, 8, 32 };
        Assert.Equal(12 + 8 * 9, off[0]);
        for (int i = 0; i < 9; i++)
        {
            long need = i == 0 ? 2 * 8 : widths[i];
            long end = i + 1 < 9 ? off[i + 1] : s.Length;
            Assert.True(end - off[i] >= need, $"section {i} too small");
        }
        long I64(long at) => BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan((int)at));
        Assert.Equal("dir/x", Encoding.UTF8.GetString(s, (int)off[1], 5));
        Assert.Equal(Sample.Size, I64(off[2]));
        Assert.Equal(Sample.MTimeTicks, I64(off[3]));
        Assert.Equal(Sample.ContentHash, Convert.ToHexString(s, (int)off[4], 16));
        Assert.Equal(Sample.ChangeTicks, I64(off[5]));
        Assert.Equal(Sample.FileId, I64(off[6]));
        Assert.Equal(Sample.HashedAtTicks, I64(off[7]));
        Assert.True(s.Skip((int)off[8]).Take(32).All(b => b == 0) && s.Length >= off[8] + 32); // SHA-256 not computed
    }

    // Ledgers written by earlier versions (v1) must keep loading: their entries just carry no change time / file id.
    [Fact]
    public void Base_V1_StillReads()
    {
        using var tmp = new TempRepo();
        var path = Path.Combine(tmp.Root, "snapshot-00000000.base");
        File.WriteAllBytes(path, V1Base("a.c", 10, 555, "00112233445566778899AABBCCDDEEFF"));
        using var r = new SnapshotBaseReader(path);
        Assert.Equal(new FileState(10, 555, "00112233445566778899AABBCCDDEEFF"), r.GetState(0));
        Assert.Equal("a.c", r.GetPath(0));
    }

    [Fact]
    public void Journal_V2_RoundTrips_AndAV1JournalStillLoads()
    {
        using var tmp = new TempRepo();
        var dir = Path.Combine(tmp.Root, "cache");
        DiskSnapshot.WriteFullBase(dir, new Dictionary<string, FileState> { ["a"] = Sample });
        using (var snap = DiskSnapshot.Open(dir))
        {
            snap["b"] = Sample with { Size = 7 };
            snap.Save(); // journal
        }
        var j = File.ReadAllBytes(Path.Combine(dir, "snapshot.journal"));
        Assert.Equal(0x324E5343u, BinaryPrimitives.ReadUInt32LittleEndian(j)); // "CSN2"
        using (var snap = DiskSnapshot.Open(dir))
        {
            Assert.True(snap.TryGetValue("b", out var b));
            Assert.Equal(Sample with { Size = 7 }, b);
        }

        // A v1 journal ("CSNJ": path, size, mtime, hash) left by an older version.
        using (var fs = File.Create(Path.Combine(dir, "snapshot.journal")))
        using (var w = new BinaryWriter(fs, Encoding.UTF8))
        {
            w.Write(0x4A4E5343u); w.Write(1);
            w.Write("c"); w.Write(9L); w.Write(99L); w.Write("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");
            w.Write(0);
        }
        using (var snap = DiskSnapshot.Open(dir))
        {
            Assert.True(snap.TryGetValue("c", out var c));
            Assert.Equal(new FileState(9, 99, FileState.BinaryHash), c);
        }
    }

    private static byte[] V1Base(string p, long size, long mtime, string hash)
    {
        var pb = Encoding.UTF8.GetBytes(p);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        long offs = 12 + 8 * 5, blob = offs + 16, sizes = blob + pb.Length, mtimes = sizes + 8, hashes = mtimes + 8;
        w.Write(0x4E535343u); w.Write(1); w.Write(1);
        w.Write(offs); w.Write(blob); w.Write(sizes); w.Write(mtimes); w.Write(hashes);
        w.Write(0L); w.Write((long)pb.Length); w.Write(pb); w.Write(size); w.Write(mtime); w.Write(Convert.FromHexString(hash));
        return ms.ToArray();
    }

    // ---- the decision with change time + file id (LedgerTrust.Record) ----

    private static readonly long ReadStart = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long RecordedAt = ReadStart + TimeSpan.FromMilliseconds(5).Ticks;
    private static readonly long Old = ReadStart - TimeSpan.FromMinutes(10).Ticks;
    private static readonly long Recent = ReadStart - TimeSpan.FromSeconds(1).Ticks;

    private static FileState Rec(FileIdentity before, FileIdentity after, long listedSize = 10, long listedMTime = 0,
                                 FileState? prior = null, long readStart = 0, long priorRecordedBy = 0, int readSize = 10) =>
        LedgerTrust.Record(new ReadStamp(listedSize, listedMTime == 0 ? before.MTimeTicks : listedMTime, before, after,
                                         readStart == 0 ? ReadStart : readStart, RecordedAt),
                           network: false, prior, priorRecordedBy, readSize, "H");

    [Fact]
    public void Settled_IsTrusted_WithTheHandleIdentity_AndHashedAtIsTheReadStart()
    {
        var id = new FileIdentity(10, Old, Old, 42);
        Assert.Equal(new FileState(10, Old, "H", Old, 42, ReadStart), Rec(id, id));
    }

    // A recent CHANGE time (metadata changed - e.g. a modified time set back to an old value) is not settled, even with an
    // old modified time: that is exactly the "restored mtime" edit the change time exists to catch.
    [Fact]
    public void RecentChangeTime_OldModifiedTime_IsPending()
    {
        var id = new FileIdentity(10, Old, Recent, 42);
        Assert.Equal(new FileState(10, -Old, "H", Recent, 42, RecordedAt), Rec(id, id));
    }

    // Stats taken through the read handle before and after the read must agree with each other and with the listing;
    // otherwise the bytes may not be what those stamps describe, and nothing is vouched for (mtime 0, no change time).
    [Theory]
    [InlineData("after-mtime")]
    [InlineData("after-ctime")]
    [InlineData("after-size")]
    [InlineData("listing-mtime")]
    [InlineData("listing-size")]
    [InlineData("read-size")]
    public void UnstableRead_IsRecordedUnknown(string what)
    {
        var before = new FileIdentity(10, Old, Old, 42);
        var after = what switch
        {
            "after-mtime" => before with { MTimeTicks = Old + 1 },
            "after-ctime" => before with { ChangeTicks = Old + 1 },
            "after-size" => before with { Size = 11 },
            _ => before,
        };
        var st = Rec(before, after,
                     listedSize: what == "listing-size" ? 9 : 10,
                     listedMTime: what == "listing-mtime" ? Old - 1 : Old,
                     readSize: what == "read-size" ? 9 : 10);
        Assert.Equal(0, st.MTimeTicks);
        Assert.Equal(0, st.ChangeTicks);
    }

    // No handle identity (non-Windows): the modified-time rule alone, change time / file id unknown (0).
    [Fact]
    public void UnknownIdentity_FallsBackToTheModifiedTimeRule()
    {
        var st = LedgerTrust.Record(new ReadStamp(10, Old, default, default, ReadStart, RecordedAt), network: false, null, 0, 10, "H");
        Assert.Equal(new FileState(10, Old, "H", 0, 0, ReadStart), st);
    }

    // Second look: anchored on the pending entry's OWN recorded time (our clock), and the change time + file id must be
    // unchanged too.
    [Fact]
    public void SecondLook_UsesTheEntrysRecordedTime_AndNeedsTheSameIdentity()
    {
        // A future stamp (a share clock ahead of ours): the first look never passes, so only the second look can trust it.
        long future = ReadStart + TimeSpan.FromHours(2).Ticks;
        var id = new FileIdentity(10, future, future, 42);
        var pending = new FileState(10, -future, "H", future, 42, HashedAtTicks: ReadStart);
        long later = ReadStart + TimeSpan.FromSeconds(5).Ticks, soon = ReadStart + TimeSpan.FromSeconds(1).Ticks;

        Assert.Equal(future, Rec(id, id, prior: pending, readStart: later).MTimeTicks);
        Assert.Equal(-future, Rec(id, id, prior: pending, readStart: soon).MTimeTicks);
        var moved = id with { ChangeTicks = future + 1 };
        Assert.Equal(-future, Rec(moved, moved, prior: pending, readStart: later).MTimeTicks);
        var other = id with { FileId = 43 };
        Assert.Equal(-future, Rec(other, other, prior: pending, readStart: later).MTimeTicks);
    }

    // A pending entry's own recorded time is on the clock of whichever machine recorded it. With the cache dir on a share
    // (CODECOMPASS_CACHE_DIR), another machine - whose clock may be behind - may have recorded it, so the second look
    // falls back to the caller's own-process bound instead (review of the v2 change, finding 3).
    [Fact]
    public void SecondLook_SharedCacheDir_DoesNotUseTheEntrysClock()
    {
        long future = ReadStart + TimeSpan.FromHours(2).Ticks;
        var id = new FileIdentity(10, future, future, 42);
        var pending = new FileState(10, -future, "H", future, 42, HashedAtTicks: ReadStart);
        long later = ReadStart + TimeSpan.FromSeconds(5).Ticks;
        FileState Shared(long bound) => LedgerTrust.Record(new ReadStamp(10, future, id, id, later, later), network: false,
                                                           pending, bound, 10, "H", entryClockIsOurs: false);
        Assert.Equal(-future, Shared(0).MTimeTicks);                                         // no own bound: not trusted
        Assert.Equal(future, Shared(ReadStart).MTimeTicks);                                  // own bound 5 s back: trusted
        Assert.Equal(-future, Shared(later - TimeSpan.FromSeconds(1).Ticks).MTimeTicks);     // own bound too recent
    }

    // ---- what a build records ----

    // Checked against sources independent of FileIdentity's own queries: creation, access and write times are set to
    // distinct old dates, so only the CHANGE time is recent (setting them is a metadata change) - reading any other
    // field would land on an old date. The file id is compared with what `fsutil file queryfileid` reports.
    [Fact]
    public void Build_RecordsTheRealChangeTimeAndFileId()
    {
        if (!OperatingSystem.IsWindows()) return; // change time / file id are only read on Windows
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        File.WriteAllText(path, "token_v2_independent");
        long t0 = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(1).Ticks;
        File.SetCreationTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(path, new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();

        Assert.True(DiskSnapshot.TryOpen(IndexStore.CacheDirPath(repo.Root), out var snap));
        using (snap)
        {
            Assert.True(snap.TryGetValue("a.txt", out var st));
            Assert.InRange(st.ChangeTicks, t0, DateTime.UtcNow.Ticks);
            Assert.Equal(-new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, st.MTimeTicks); // recent change: pending

            var psi = new System.Diagnostics.ProcessStartInfo("fsutil", $"file queryfileid \"{path}\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var m = System.Text.RegularExpressions.Regex.Match(output, "0x([0-9a-fA-F]{16,32})");
            Assert.True(m.Success, "fsutil output: " + output);
            var hex = m.Groups[1].Value;
            Assert.Equal(Convert.ToInt64(hex[^16..], 16), st.FileId); // NTFS: the low 64 bits are the file index
        }
    }

    // Every hashed file gets its change time, file id and hashed-at, read from the handle it was read through (Windows;
    // other platforms record 0 = unknown, which CodeDiffer never trusts).
    [Fact]
    public void Build_RecordsChangeTimeFileIdAndHashedAt()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "token_v2");
        long before = DateTime.UtcNow.Ticks;
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
        long after = DateTime.UtcNow.Ticks;

        Assert.True(DiskSnapshot.TryOpen(IndexStore.CacheDirPath(repo.Root), out var snap));
        using (snap)
        {
            Assert.True(snap.TryGetValue("a.txt", out var st));
            Assert.InRange(st.HashedAtTicks, before, after);
            if (OperatingSystem.IsWindows())
            {
                var live = FileIdentity.OfPath(repo.FullPath("a.txt"));
                Assert.NotEqual(0, live.ChangeTicks);
                Assert.Equal(live.ChangeTicks, st.ChangeTicks);
                Assert.Equal(live.FileId, st.FileId);
                Assert.NotEqual(0, st.FileId);
            }
        }
    }
}
