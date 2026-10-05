using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// The change ledger skips a file whose size + modified time match what was hashed. Two holes (found in a review with
// CodeDiffer, 2026-10-05):
//  1. "Racily clean": on a filesystem with coarse timestamps (FAT/exFAT 2 s, some NAS 1 s) an edit in the same tick as
//     the hash leaves size + mtime unchanged. The old guard compared each file's mtime with the LEDGER's last write time,
//     which every later write moves forward - so an entry that was racy when hashed later looked safe.
//  2. That guard compared the SERVER's file times against OUR clock (wrong when they disagree), and nothing noticed a
//     file that changed while it was being read.
// The fix decides when the entry is RECORDED, from the file's own timestamp: an entry is only trusted if it can't be
// racy, so nothing depends on the ledger's write time or on comparing clocks.
public class LedgerTrustTests
{
    private static long WholeSecond(DateTime utc) => utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond;

    private static void WriteWithMtime(string path, string text, long mtimeTicks)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, new DateTime(mtimeTicks, DateTimeKind.Utc));
    }

    private static void BuildIndex(string root)
    {
        var (t, s, _) = RepositoryIndexer.Build(root);
        t.Dispose(); s.Dispose();
    }

    private static bool Finds(string root, string token)
    {
        Assert.True(RepositoryIndexer.TryLoad(root, out var text, out var symbols));
        using (text) using (symbols) return text.Search(token).Any();
    }

    // A coarse (whole-second) timestamp from the last hour can't be trusted: the same-size edit made in that same second
    // must be picked up by the next update.
    [Fact]
    public void Update_SameSizeEdit_WithinTheSameCoarseTick_IsReindexed()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        long tick = WholeSecond(DateTime.UtcNow.AddSeconds(-30));
        WriteWithMtime(path, "token_alpha_one", tick);
        BuildIndex(repo.Root);

        WriteWithMtime(path, "token_alpha_two", tick); // same size, same (coarse) mtime
        var (t, s, _) = RepositoryIndexer.Update(repo.Root);
        t.Dispose(); s.Dispose();

        Assert.True(Finds(repo.Root, "token_alpha_two"), "the same-tick edit must be re-indexed, not trusted as unchanged");
    }

    // Bug 1 exactly: an entry that was racy when hashed must not become trusted because a LATER ledger write (here, an
    // update with no changes) moved the ledger's write time forward.
    [Fact]
    public void Update_RacyEntry_IsNotTrustedAfterALaterLedgerWrite()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        long tick = WholeSecond(DateTime.UtcNow);
        WriteWithMtime(path, "token_beta_one", tick);
        BuildIndex(repo.Root);

        System.Threading.Thread.Sleep(3000);
        var (t1, s1, _) = RepositoryIndexer.Update(repo.Root); // nothing changed, but the ledger is written again
        t1.Dispose(); s1.Dispose();

        WriteWithMtime(path, "token_beta_two", tick);
        var (t2, s2, _) = RepositoryIndexer.Update(repo.Root);
        t2.Dispose(); s2.Dispose();

        Assert.True(Finds(repo.Root, "token_beta_two"));
    }

    // Fine-grained timestamps (NTFS, ext4/xfs behind Samba) can't be racy: an unchanged file keeps its recorded mtime, so
    // updates skip it without reading it.
    [Fact]
    public void FineGrainedTimestamp_IsRecorded_SoUpdatesSkipTheFile()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        WriteWithMtime(path, "token_gamma", DateTime.UtcNow.AddSeconds(-30).Ticks | 1234567); // sub-second part
        BuildIndex(repo.Root);

        Assert.True(DiskSnapshot.TryOpen(IndexStore.CacheDirPath(repo.Root), out var snap));
        using (snap)
        {
            Assert.True(snap.TryGetValue("a.txt", out var st));
            Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, st.MTimeTicks);
        }
    }

    // Files at/over the streaming threshold (128 MB) take a separate read path in both build and update; the same trust
    // decision must apply there.
    private static void WriteLarge(string path, string tail, byte fill, long mtimeTicks)
    {
        var chunk = new byte[1 << 20];
        Array.Fill(chunk, fill);
        if (fill != 0) for (int i = 99; i < chunk.Length; i += 100) chunk[i] = (byte)'\n'; // text: give it lines
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            long need = LargeFileIndexer.StreamThresholdBytes + (2 << 20);
            for (long w = 0; w < need; w += chunk.Length) fs.Write(chunk, 0, chunk.Length);
            var t = System.Text.Encoding.ASCII.GetBytes("\n" + tail + "\n");
            fs.Write(t, 0, t.Length);
        }
        File.SetLastWriteTimeUtc(path, new DateTime(mtimeTicks, DateTimeKind.Utc));
    }

    [Fact]
    public void StreamedLargeFile_SameTickEdit_IsReindexedByUpdate()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("big.txt");
        long tick = WholeSecond(DateTime.UtcNow.AddSeconds(-30));
        WriteLarge(path, "token_big_one", (byte)'a', tick);
        BuildIndex(repo.Root);

        WriteLarge(path, "token_big_two", (byte)'a', tick); // same size, same coarse tick
        var (t, s, _) = RepositoryIndexer.Update(repo.Root);
        t.Dispose(); s.Dispose();

        Assert.True(Finds(repo.Root, "token_big_two"));
    }

    [Fact]
    public void StreamedLargeBinaryFile_IsRecordedAsBinary_WithTheSameTrustRule()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("blob.txt"); // a text extension, binary CONTENT: detection by content, not by name
        long tick = WholeSecond(DateTime.UtcNow.AddSeconds(-30));
        WriteLarge(path, "x", 0, tick); // NUL bytes: binary
        BuildIndex(repo.Root);
        var (t, s, _) = RepositoryIndexer.Update(repo.Root); // recent coarse entry: re-read, still binary
        t.Dispose(); s.Dispose();

        Assert.True(DiskSnapshot.TryOpen(IndexStore.CacheDirPath(repo.Root), out var snap));
        using (snap)
        {
            Assert.True(snap.TryGetValue("blob.txt", out var st));
            Assert.True(st.IsBinary);
            Assert.Equal(0, st.MTimeTicks); // recent + coarse: recorded as unknown
        }
    }

    // ---- the decision itself ----

    [Fact]
    public void RecordedMTime_UnreadablePath_IsUnknown()
    {
        long recent = DateTime.UtcNow.AddMinutes(-5).Ticks | 1234567;
        Assert.Equal(0, LedgerTrust.RecordedMTime("bad\0path.txt", 1, recent, DateTime.UtcNow.Ticks));
    }

    [Fact]
    public void RecordedMTime_OldFile_IsTrustedWithoutAnotherStat()
    {
        long now = DateTime.UtcNow.Ticks;
        long old = WholeSecond(DateTime.UtcNow.AddDays(-3)); // whole-second, but long ago: nothing is writing it now
        Assert.Equal(old, LedgerTrust.RecordedMTime(@"Z:\does\not\exist.txt", 10, old, now));
    }

    [Fact]
    public void RecordedMTime_RecentCoarseTimestamp_IsUnknown()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        long tick = WholeSecond(DateTime.UtcNow.AddMinutes(-5));
        WriteWithMtime(path, "x", tick);
        Assert.Equal(0, LedgerTrust.RecordedMTime(path, 1, tick, DateTime.UtcNow.Ticks));
    }

    [Fact]
    public void RecordedMTime_RecentFileThatChangedWhileBeingRead_IsUnknown()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        long listed = DateTime.UtcNow.AddMinutes(-5).Ticks | 1234567;
        WriteWithMtime(path, "x", listed);
        File.WriteAllText(path, "xy"); // edited after it was listed: new size and mtime
        Assert.Equal(0, LedgerTrust.RecordedMTime(path, 1, listed, DateTime.UtcNow.Ticks));
    }

    [Fact]
    public void RecordedMTime_RecentFineGrainedUnchangedFile_IsTrusted()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        long listed = DateTime.UtcNow.AddMinutes(-5).Ticks | 1234567;
        WriteWithMtime(path, "x", listed);
        long actual = File.GetLastWriteTimeUtc(path).Ticks;
        Assert.Equal(actual, LedgerTrust.RecordedMTime(path, 1, actual, DateTime.UtcNow.Ticks));
    }
}
