using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// The change ledger skips a file whose size + modified time match what was hashed. A same-size edit in the same timestamp
// tick as the read ("racily clean") keeps both - and timestamps tick coarsely everywhere: FAT/exFAT 2 s, some NAS 1 s, and
// even NTFS only advances on a ~1-16 ms timer, so back-to-back rewrites keep one sub-second stamp (measured in review).
//
// Rule (LedgerTrust): an entry is trusted only if the file was last modified safely BEFORE we started reading it - by a
// margin that covers the coarsest tick locally, and an hour on a network share, whose clock we can't read. Otherwise its
// mtime is recorded negated ("pending": matches no file) and the next update re-reads it; that later read trusts it if the
// stamp and size are unchanged and it began more than the margin after the first recording (both on our clock). Decided
// when the entry is RECORDED, so a later ledger write can't make a racy entry look safe.
[Collection("compaction-env")] // one test sets the process-wide CODECOMPASS_FORCE_NETWORK
public class LedgerTrustTests
{
    private static long WholeSecond(DateTime utc) => utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond;

    private static void BuildIndex(string root)
    {
        var (t, s, _) = RepositoryIndexer.Build(root);
        t.Dispose(); s.Dispose();
    }

    private static void UpdateIndex(string root)
    {
        var (t, s, _) = RepositoryIndexer.Update(root);
        t.Dispose(); s.Dispose();
    }

    private static bool Finds(string root, string token)
    {
        Assert.True(RepositoryIndexer.TryLoad(root, out var text, out var symbols));
        using (text) using (symbols) return text.Search(token).Any();
    }

    // Same tick as the read: rewrite at the same size and keep the timestamp the file had when it was hashed.
    private static void SameTickEdit(string path, string text)
    {
        var mtime = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, mtime);
    }

    [Fact]
    public void RacyEdit_FineGrainedTimestamp_IsPickedUpByUpdate()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "token_alpha_one");     // NTFS: a sub-second stamp
        BuildIndex(repo.Root);
        SameTickEdit(repo.FullPath("a.txt"), "token_alpha_two");
        UpdateIndex(repo.Root);
        Assert.True(Finds(repo.Root, "token_alpha_two"));
    }

    [Fact]
    public void RacyEdit_CoarseTimestamp_IsPickedUpByUpdate()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        File.WriteAllText(path, "token_coarse_one");
        File.SetLastWriteTimeUtc(path, new DateTime(WholeSecond(DateTime.UtcNow), DateTimeKind.Utc)); // FAT-like stamp
        BuildIndex(repo.Root);
        SameTickEdit(path, "token_coarse_two");
        UpdateIndex(repo.Root);
        Assert.True(Finds(repo.Root, "token_coarse_two"));
    }

    // Bug 1 exactly: the racy entry must stay untrusted even after a LATER ledger write (here a live edit of another file,
    // which writes the ledger without re-checking a.txt). The old rule then compared a.txt's mtime against that newer
    // write time and trusted the stale hash.
    [Fact]
    public void RacyEntry_IsNotTrustedAfterALaterLedgerWrite()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "token_beta_one");
        repo.Write("b.txt", "other");
        BuildIndex(repo.Root);
        SameTickEdit(repo.FullPath("a.txt"), "token_beta_two");

        System.Threading.Thread.Sleep(3500);
        repo.Write("b.txt", "other changed");
        var dir = IndexStore.GetCacheDir(repo.Root);
        using (IndexWriteLock.Acquire(dir))
        {
            Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
            using (text) using (symbols)
            using (var snap = DiskSnapshot.Open(dir))
            {
                RepositoryIndexer.ApplyChanges(text, symbols, snap, repo.Root, new[] { repo.FullPath("b.txt") });
                RepositoryIndexer.Persist(repo.Root, text, symbols, snap);
            }
        }

        UpdateIndex(repo.Root);
        Assert.True(Finds(repo.Root, "token_beta_two"));
    }

    // A file last modified well before the read is trusted, so updates skip it without reading it.
    [Fact]
    public void SettledFile_KeepsItsMtime_SoUpdatesSkipIt()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        File.WriteAllText(path, "token_gamma");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        BuildIndex(repo.Root);

        Assert.True(DiskSnapshot.TryOpen(IndexStore.CacheDirPath(repo.Root), out var snap));
        using (snap)
        {
            Assert.True(snap.TryGetValue("a.txt", out var st));
            Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, st.MTimeTicks);
        }
    }

    // ---- files over the streaming threshold (128 MB) take a separate read path in build and update ----

    private static void WriteLarge(string path, string tail, byte fill)
    {
        var chunk = new byte[1 << 20];
        Array.Fill(chunk, fill);
        if (fill != 0) for (int i = 99; i < chunk.Length; i += 100) chunk[i] = (byte)'\n'; // text: give it lines
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        long need = LargeFileIndexer.StreamThresholdBytes + (2 << 20);
        for (long w = 0; w < need; w += chunk.Length) fs.Write(chunk, 0, chunk.Length);
        var t = System.Text.Encoding.ASCII.GetBytes("\n" + tail + "\n");
        fs.Write(t, 0, t.Length);
    }

    [Fact]
    public void StreamedLargeFile_RacyEdit_IsReindexedByUpdate()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("big.txt");
        WriteLarge(path, "token_big_one", (byte)'a');
        BuildIndex(repo.Root);

        var mtime = File.GetLastWriteTimeUtc(path);
        WriteLarge(path, "token_big_two", (byte)'a'); // same size
        File.SetLastWriteTimeUtc(path, mtime);
        UpdateIndex(repo.Root);

        Assert.True(Finds(repo.Root, "token_big_two"));
    }

    [Fact]
    public void StreamedLargeBinaryFile_IsRecordedAsBinary_WithTheSameTrustRule()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("blob.txt"); // a text extension, binary CONTENT: detection by content, not by name
        WriteLarge(path, "x", 0);
        BuildIndex(repo.Root);
        UpdateIndex(repo.Root); // still recent: re-read, still binary

        Assert.True(DiskSnapshot.TryOpen(IndexStore.CacheDirPath(repo.Root), out var snap));
        using (snap)
        {
            Assert.True(snap.TryGetValue("blob.txt", out var st));
            Assert.True(st.IsBinary);
            Assert.Equal(-File.GetLastWriteTimeUtc(path).Ticks, st.MTimeTicks); // modified moments before the read: pending
        }
    }

    // ---- the decision itself ----

    private static readonly long ReadStart = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void Decision_SettledBeforeTheRead_IsTrusted()
    {
        long mtime = ReadStart - TimeSpan.FromMinutes(5).Ticks;
        Assert.Equal(mtime, LedgerTrust.RecordedMTime(mtime, ReadStart, network: false));
    }

    // Not yet trusted: recorded as PENDING, the negated observed mtime - it matches no file (so the next update re-reads it)
    // but remembers what was seen, so a later unchanged observation can trust it (below).
    [Theory]
    [InlineData(-1)]       // a second before the read: within the coarsest tick
    [InlineData(0)]        // at the read
    [InlineData(5)]        // after the read started (or a server clock ahead of ours)
    public void Decision_ModifiedAroundTheRead_IsPending(int secondsFromRead)
    {
        long mtime = ReadStart + TimeSpan.FromSeconds(secondsFromRead).Ticks;
        Assert.Equal(-mtime, LedgerTrust.RecordedMTime(mtime, ReadStart, network: false));
    }

    // A share's clock may differ from ours: within the hour is untrusted there, while locally the same file is settled.
    [Fact]
    public void Decision_NetworkRoot_UsesAnHourMargin()
    {
        long mtime = ReadStart - TimeSpan.FromMinutes(10).Ticks;
        Assert.Equal(-mtime, LedgerTrust.RecordedMTime(mtime, ReadStart, network: true));
        Assert.Equal(mtime, LedgerTrust.RecordedMTime(mtime, ReadStart, network: false));
        long old = ReadStart - TimeSpan.FromHours(2).Ticks;
        Assert.Equal(old, LedgerTrust.RecordedMTime(old, ReadStart, network: true));
    }

    [Fact]
    public void Decision_UnknownListedTime_StaysUnknown() =>
        Assert.Equal(0, LedgerTrust.RecordedMTime(0, ReadStart, network: false));

    // ---- the second look: clock-independent trust for files the margin can't vouch for (review finding 3) ----
    // A file whose stamp is in the future (a share whose clock runs ahead, a NAS without time sync, a future-dated file)
    // never passes the margin, so the old rule re-read it on every update forever - the whole tree on a share an hour
    // ahead. The stamp was already on the file when we first listed it, so its tick had started by then, and ends within
    // one tick. A later read that starts more than the margin after that first recording - both times on OUR clock - and
    // still sees the same stamp and size reads bytes no later write can share that stamp with. So it's trusted.

    private static readonly long Future = ReadStart + TimeSpan.FromHours(2).Ticks;

    [Fact]
    public void Decision_FutureStamp_SeenUnchangedAfterTheMargin_IsTrusted()
    {
        var prior = new FileState(10, -Future, "H");
        long recordedBy = ReadStart;
        long later = recordedBy + TimeSpan.FromSeconds(5).Ticks;
        Assert.Equal(Future, LedgerTrust.RecordedMTime(Future, later, network: false, prior, recordedBy, recordedSize: 10));
        Assert.Equal(Future, LedgerTrust.RecordedMTime(Future, later, network: true, prior, recordedBy, recordedSize: 10));
    }

    [Fact]
    public void Decision_SecondLook_TooSoon_StaysPending()
    {
        var prior = new FileState(10, -Future, "H");
        long soon = ReadStart + TimeSpan.FromSeconds(1).Ticks;
        Assert.Equal(-Future, LedgerTrust.RecordedMTime(Future, soon, network: false, prior, ReadStart, recordedSize: 10));
    }

    [Fact]
    public void Decision_SecondLook_StampOrSizeChanged_StaysPending()
    {
        var prior = new FileState(10, -Future, "H");
        long later = ReadStart + TimeSpan.FromSeconds(5).Ticks;
        long moved = Future + 1;
        Assert.Equal(-moved, LedgerTrust.RecordedMTime(moved, later, network: false, prior, ReadStart, recordedSize: 10));
        Assert.Equal(-Future, LedgerTrust.RecordedMTime(Future, later, network: false, prior, ReadStart, recordedSize: 11));
    }

    // A trusted or unknown prior is not a first look: only a PENDING prior (negative) carries the observed stamp.
    [Fact]
    public void Decision_SecondLook_NeedsAPendingPrior()
    {
        long later = ReadStart + TimeSpan.FromSeconds(5).Ticks;
        Assert.Equal(-Future, LedgerTrust.RecordedMTime(Future, later, network: false, new FileState(10, 0, "H"), ReadStart, recordedSize: 10));
        Assert.Equal(-Future, LedgerTrust.RecordedMTime(Future, later, network: false, new FileState(10, Future, "H"), ReadStart, recordedSize: 10));
    }

    // End to end: a future-dated file is pending after the build, trusted by the first update made after the margin, and
    // then skipped like any settled file. The old rule left it at 0 (re-read) forever.
    [Fact]
    public void FutureDatedFile_IsTrustedByALaterUpdate()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        File.WriteAllText(path, "token_future");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(2));
        long stamp = File.GetLastWriteTimeUtc(path).Ticks;
        BuildIndex(repo.Root);
        Assert.Equal(-stamp, RecordedTicks(repo.Root, "a.txt"));

        System.Threading.Thread.Sleep(3500);
        UpdateIndex(repo.Root);
        Assert.Equal(stamp, RecordedTicks(repo.Root, "a.txt"));
        Assert.True(Finds(repo.Root, "token_future"));
    }

    // The watcher path (ApplyChanges on a live snapshot) takes the same second look.
    [Fact]
    public void FutureDatedFile_IsTrustedByALaterLiveChange()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        File.WriteAllText(path, "token_future_live");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(2));
        long stamp = File.GetLastWriteTimeUtc(path).Ticks;
        BuildIndex(repo.Root);

        System.Threading.Thread.Sleep(3500);
        var dir = IndexStore.GetCacheDir(repo.Root);
        using (IndexWriteLock.Acquire(dir))
        {
            Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var text, out var symbols));
            using (text) using (symbols)
            using (var snap = DiskSnapshot.Open(dir))
            {
                RepositoryIndexer.ApplyChanges(text, symbols, snap, repo.Root, new[] { path });
                RepositoryIndexer.Persist(repo.Root, text, symbols, snap);
            }
        }
        Assert.Equal(stamp, RecordedTicks(repo.Root, "a.txt"));
    }

    // Two updates back to back (inside the margin) must NOT trust it yet: the first recording may be moments old.
    [Fact]
    public void FutureDatedFile_BackToBackUpdates_StayPending()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        File.WriteAllText(path, "token_future_soon");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(2));
        long stamp = File.GetLastWriteTimeUtc(path).Ticks;
        BuildIndex(repo.Root);
        UpdateIndex(repo.Root);
        Assert.Equal(-stamp, RecordedTicks(repo.Root, "a.txt"));
    }

    // Review round 3: the bound comes from the ledger files' write times, which are on OUR clock only when the cache dir is
    // local. CODECOMPASS_CACHE_DIR may point at a share whose clock is behind, making the bound too early and trusting a
    // racy entry. A network cache dir gives no file-time bound: only this process's own upserts count.
    [Fact]
    public void NetworkCacheDir_GivesNoFileTimeBound()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "x");
        BuildIndex(repo.Root);
        var dir = IndexStore.CacheDirPath(repo.Root);
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1");
            Assert.True(DiskSnapshot.TryOpen(dir, out var snap));
            using (snap)
            {
                Assert.Equal(0, snap.RecordedByUtcTicks);
                long before = DateTime.UtcNow.Ticks;
                snap["b.txt"] = new FileState(1, 0, "H");
                Assert.True(snap.RecordedByUtcTicks >= before);
            }
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", old); }
    }

    private static long RecordedTicks(string root, string rel)
    {
        Assert.True(DiskSnapshot.TryOpen(IndexStore.CacheDirPath(root), out var snap));
        using (snap)
        {
            Assert.True(snap.TryGetValue(rel, out var st));
            return st.MTimeTicks;
        }
    }
}
