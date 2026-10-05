using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Changes.Segments;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// Review WP-F: crash-consistency and load-failure handling of the on-disk index.
public class DurabilityTests
{
    private static string CacheDir(TempRepo r) => IndexStore.GetCacheDir(r.Root);

    // P1-14: manifest and tombstones were two separate atomic replaces; a crash between them reloaded the new segment
    // list WITHOUT that batch's deletions (edited files matched old+new content, deleted files came back). The manifest
    // now carries the tombstones, so losing the separate file can't resurrect anything.
    [Fact]
    public void Tombstones_AreCommittedWithTheManifest()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class KeepMe { }");
        repo.Write("b.cs", "class DeletedThing { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        repo.Delete("b.cs");
        var u = RepositoryIndexer.Update(repo.Root);
        u.Text.Dispose(); u.Symbols.Dispose();

        File.Delete(Path.Combine(CacheDir(repo), "segments.tombstones"));   // the "crash before the 2nd write" state
        File.Delete(Path.Combine(CacheDir(repo), "symbols.tombstones"));

        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
        using (t) using (s)
        {
            // On the index itself - a search would mask the bug, since its verify step re-reads (missing) b.cs.
            Assert.DoesNotContain("b.cs", t.AllPaths());
            Assert.Equal(1, t.DocumentCount);
            Assert.Empty(s.FindByName("DeletedThing"));
            Assert.NotEmpty(t.Search("KeepMe"));
        }
    }

    // P1-13: a manifest-named segment that's missing used to be SKIPPED - the index loaded and served without that
    // segment's documents, silently. A persistent gap is now corruption (rebuild), never a partial "success".
    [Fact]
    public void ManifestNamingAMissingSegment_IsCorrupt_NotSilentlyPartial()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        foreach (var seg in Directory.GetFiles(CacheDir(repo), "seg-*.ccseg")) File.Delete(seg);

        Assert.False(RepositoryIndexer.TryLoad(repo.Root, out _, out _, out var why));
        Assert.Equal(IndexLoadFailure.Corrupt, why);
    }

    // P1-13: a transient read failure (a lock held a moment by AV/backup/another process) must not make the update
    // path throw away a perfectly good index and rebuild the whole repo.
    [Fact]
    public void TransientlyUnreadableIndex_IsReported_NotRebuilt()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var manifest = Path.Combine(CacheDir(repo), "segments.manifest");
        var before = File.GetLastWriteTimeUtc(manifest);

        using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(RepositoryIndexer.TryLoad(repo.Root, out _, out _, out var why));
            Assert.Equal(IndexLoadFailure.Transient, why);
            Assert.Throws<IOException>(() => RepositoryIndexer.Update(repo.Root));
        }
        Assert.Equal(before, File.GetLastWriteTimeUtc(manifest)); // nothing was rebuilt over it
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var t, out var s));
        t.Dispose(); s.Dispose();
    }

    // P2-18: an index from a NEWER CodeCompass (another install updated first) was treated as corrupt and rebuilt -
    // so two installs sharing a cache would rebuild each other's index forever. Recognize it and refuse.
    [Fact]
    public void IndexFromANewerFormat_IsRecognized_AndNotRebuiltOver()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var seg = Directory.GetFiles(CacheDir(repo), "seg-*.ccseg").First();
        using (var fs = new FileStream(seg, FileMode.Open, FileAccess.Write)) { fs.Position = 4; fs.Write(BitConverter.GetBytes(99)); }

        Assert.False(RepositoryIndexer.TryLoad(repo.Root, out _, out _, out var why));
        Assert.Equal(IndexLoadFailure.NewerFormat, why);
        Assert.Throws<InvalidOperationException>(() => RepositoryIndexer.Update(repo.Root));
        Assert.True(File.Exists(seg)); // not rebuilt over

        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            var r = CodeCompass.Mcp.CodeCompassTools.SearchCode("class A");
            Assert.Contains("NEWER CodeCompass", r);             // the server says why...
            Assert.True(File.Exists(seg));                       // ...and didn't start a background rebuild over it
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    // P2-16: Load only recomputed the next segment number when it fell below the loaded COUNT. A segment written by a
    // flush that crashed before the manifest recorded it then got its number reused (FileMode.Create over a file a
    // reader may have mapped).
    [Fact]
    public void NextSegmentNumber_NeverReusesANumberAlreadyOnDisk()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var dir = CacheDir(repo);
        var stray = Path.Combine(dir, SegmentedIndex.SegmentFileName(50));    // the crashed flush's leftover
        File.Copy(Directory.GetFiles(dir, "seg-*.ccseg").First(), stray);
        var strayBytes = File.ReadAllBytes(stray);

        using var idx = SegmentedIndex.Open(repo.Root, dir);
        idx.AddDocumentText("new.cs", "class NewOne { }");
        idx.Flush();
        Assert.Equal(strayBytes, File.ReadAllBytes(stray)); // not overwritten
        Assert.True(File.Exists(Path.Combine(dir, SegmentedIndex.SegmentFileName(51))));
    }

    // P2-17: a torn snapshot-base header (section offsets zeroed, count intact) passed the ordering checks and GetState
    // read in-bounds garbage - changed files looked unchanged. It must fail at open instead.
    [Fact]
    public void SnapshotBase_WithSectionsTooSmallForItsCount_IsRejectedAtOpen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "base.bin");
            var entries = new[] { ("a.cs", new FileState(1, 2, new string('0', 32))), ("b.cs", new FileState(3, 4, new string('1', 32))) };
            SnapshotBaseFile.Write(path, entries.Length, entries.Sum(e => e.Item1.Length), entries);
            using (var ok = new SnapshotBaseReader(path)) Assert.Equal(2, ok.Count);

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.Position = 12;                                       // zero every section offset
                fs.Write(new byte[40]);
            }
            Assert.Throws<InvalidDataException>(() => new SnapshotBaseReader(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // P3-13: a corrupt 5th varint byte's high bits were silently shifted out, decoding to a plausible wrong value.
    [Fact]
    public void Varint_RejectsOverflowingFifthByte()
    {
        var ok = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };   // exactly uint.MaxValue
        int o = 0;
        Assert.Equal(uint.MaxValue, Varint.Read(ok, ref o));
        var bad = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x1F };  // a 33rd bit
        int o2 = 0;
        Assert.Throws<InvalidDataException>(() => Varint.Read(bad, ref o2));
    }
}
