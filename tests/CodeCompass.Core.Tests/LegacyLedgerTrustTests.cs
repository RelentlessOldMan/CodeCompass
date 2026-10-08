using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Ledger v2 decides trust when an entry is recorded (LedgerTrust), replacing the old "racily clean" check that compared a
// file's mtime with when the ledger was written. Entries loaded from an older ledger carry no record time (HashedAt 0)
// and were never judged that way, so a same-size edit in the same tick as their hash would be skipped forever after an
// upgrade. Such an entry keeps the old rule: trusted only if the file was modified safely before the ledger was written.
[Collection("compaction-env")]
public class LegacyLedgerTrustTests
{
    private static void Build(string root) { var (t, s, _) = RepositoryIndexer.Build(root); t.Dispose(); s.Dispose(); }
    private static void Update(string root) { var (t, s, _) = RepositoryIndexer.Update(root); t.Dispose(); s.Dispose(); }

    private static bool Finds(string root, string token)
    {
        Assert.True(RepositoryIndexer.TryLoad(root, out var text, out var symbols));
        using (text) using (symbols) return text.Search(token).Any();
    }

    // Rewrite a.txt's entry the way an older ledger loads it: positive mtime, no change time / file id / hashed-at.
    private static void MakeLegacy(string root, string rel, string path)
    {
        var dir = IndexStore.GetCacheDir(root);
        using (IndexWriteLock.Acquire(dir))
        using (var snap = DiskSnapshot.Open(dir))
        {
            Assert.True(snap.TryGetValue(rel, out var e));
            snap[rel] = new FileState(e.Size, File.GetLastWriteTimeUtc(path).Ticks, e.ContentHash);
            snap.Save();
        }
    }

    private static void SameTickEdit(string path, string text)
    {
        var mtime = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, mtime);
    }

    [Fact]
    public void LegacyEntry_ModifiedJustBeforeTheLedgerWrite_IsReRead()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        File.WriteAllText(path, "token_legacy_one");
        Build(repo.Root);
        MakeLegacy(repo.Root, "a.txt", path);
        SameTickEdit(path, "token_legacy_two");
        Update(repo.Root);
        Assert.True(Finds(repo.Root, "token_legacy_two"));
        using var snap = DiskSnapshot.Open(IndexStore.GetCacheDir(repo.Root));
        Assert.True(snap.TryGetValue("a.txt", out var e));
        Assert.NotEqual(0, e.HashedAtTicks);                 // re-read and recorded under the v2 rules
    }

    [Fact]
    public void LegacyEntry_ModifiedLongBeforeTheLedgerWrite_IsStillTrusted()
    {
        using var repo = new TempRepo();
        var path = repo.FullPath("a.txt");
        File.WriteAllText(path, "token_settled_one");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        Build(repo.Root);
        MakeLegacy(repo.Root, "a.txt", path);
        Update(repo.Root);
        // Not re-read: a re-read would record the entry afresh (HashedAt set).
        using var snap = DiskSnapshot.Open(IndexStore.GetCacheDir(repo.Root));
        Assert.True(snap.TryGetValue("a.txt", out var e));
        Assert.Equal(0, e.HashedAtTicks);
    }

    [Theory]
    [InlineData(0L, false)]             // no bound on when the ledger was written: can't vouch
    [InlineData(1L, true)]              // written well after the file's stamp
    public void LegacyEntry_NeedsALedgerWriteBound(long boundOffsetMinutes, bool trusted)
    {
        long mtime = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var legacy = new FileState(10, mtime, "AA");
        long bound = boundOffsetMinutes == 0 ? 0 : mtime + TimeSpan.FromMinutes(boundOffsetMinutes).Ticks;
        Assert.Equal(trusted, LedgerTrust.MatchesListing(legacy, 10, mtime, bound, network: false));
    }

    [Fact]
    public void CurrentEntries_AreJudgedByTheirRecordedSign()
    {
        long mtime = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
        Assert.True(LedgerTrust.MatchesListing(new FileState(10, mtime, "AA", 0, 0, mtime + 1), 10, mtime, 0, network: false));
        Assert.False(LedgerTrust.MatchesListing(new FileState(10, -mtime, "AA", 0, 0, mtime + 1), 10, mtime, 0, network: false));
        Assert.False(LedgerTrust.MatchesListing(new FileState(10, mtime, "AA", 0, 0, mtime + 1), 11, mtime, 0, network: false));
        Assert.False(LedgerTrust.MatchesListing(new FileState(10, mtime, "AA", 0, 0, mtime + 1), 10, mtime + 1, 0, network: false));
    }
}
