using System;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Symbols.Segments;
using CodeCompass.Core.Walking;
using Xunit;

namespace CodeCompass.Core.Tests;

// Regressions from the CodeReview.md hardening pass. Each pins a specific fix so the class of bug can't
// silently return.
public class HardeningReviewTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-harden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // The over-the-wire data-loss path: an incremental Update whose walk DROPPED a directory (a transient SMB
    // failure, retried and still failed) must NOT prune that subtree - its files are merely absent from the
    // incomplete walk, which is indistinguishable from deletion. Pruning on that absence would wipe a healthy
    // subtree from a good index on a passing network hiccup.
    [Fact]
    public void Update_IncompleteWalk_DoesNotPruneDroppedSubtree()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "root");
        repo.Write("sub/b.cs", "beta");
        repo.Write("sub/c.cs", "gamma");
        var built = RepositoryIndexer.Build(repo.Root);
        built.Text.Dispose(); built.Symbols.Dispose();

        var oldNet = Environment.GetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", "1"); // enable the retry/drop-count path
            // Really delete one file AND make the whole 'sub' dir fail to read: the walk is now INCOMPLETE.
            File.Delete(repo.FullPath("sub/c.cs"));
            FileWalker.GlobalBeforeReadDirHook = dir =>
            {
                if (Path.GetFileName(dir) == "sub") throw new IOException("simulated persistent SMB failure");
            };

            var u = RepositoryIndexer.Update(repo.Root);
            try
            {
                var paths = u.Text.AllPaths();
                Assert.Contains(paths, p => p == "sub/b.cs");   // retained though absent from the incomplete walk
                Assert.Contains(paths, p => p == "sub/c.cs");   // NOT pruned even though really deleted (walk incomplete)
                Assert.Equal(0, u.Stats.Removed);               // nothing pruned when the walk was incomplete
            }
            finally { u.Text.Dispose(); u.Symbols.Dispose(); }
        }
        finally
        {
            FileWalker.GlobalBeforeReadDirHook = null;
            Environment.SetEnvironmentVariable("CODECOMPASS_FORCE_NETWORK", oldNet);
        }
    }

    // Scope check: with a COMPLETE walk, a genuinely-deleted file IS pruned - the suppression above is limited
    // to incomplete walks and doesn't break normal deletion reconciliation.
    [Fact]
    public void Update_CompleteWalk_PrunesGenuinelyDeletedFile()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "root");
        repo.Write("sub/b.cs", "beta");
        repo.Write("sub/c.cs", "gamma");
        var built = RepositoryIndexer.Build(repo.Root);
        built.Text.Dispose(); built.Symbols.Dispose();

        File.Delete(repo.FullPath("sub/c.cs"));
        var u = RepositoryIndexer.Update(repo.Root);
        try
        {
            var paths = u.Text.AllPaths();
            Assert.Contains(paths, p => p == "sub/b.cs");
            Assert.DoesNotContain(paths, p => p == "sub/c.cs"); // complete walk => genuine deletion is pruned
        }
        finally { u.Text.Dispose(); u.Symbols.Dispose(); }
    }

    // A structurally-corrupt symbol segment (a header count larger than its columns can hold) must be rejected
    // at OPEN so callers rebuild, not hit an OOB read / huge or negative-length allocation on a later query.
    // (Mirrors the guard SegmentReader already had; SymbolSegmentReader was missing it.)
    [Fact]
    public void SymbolSegmentReader_RejectsCorruptCount()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "sym.ccsym");
            var b = new SymbolSegmentBuilder();
            b.Add(new Symbol("Foo", SymbolKind.Class, "a.cs", 1, 1));
            b.WriteTo(file);
            using (var ok = new SymbolSegmentReader(file)) Assert.Equal(1, ok.Count); // sanity: a valid segment opens

            // Tamper the Count field (header offset 8) to a value the fixed-width columns can't possibly hold.
            var bytes = File.ReadAllBytes(file);
            BitConverter.GetBytes(1_000_000).CopyTo(bytes, 8);
            File.WriteAllBytes(file, bytes);

            Assert.Throws<InvalidDataException>(() => new SymbolSegmentReader(file));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // A corrupt symbol tombstone file (negative segment count) must be rejected, not fed to a loop/allocation.
    // (Mirrors SegmentedIndex.Load's guard, which SegmentedSymbolIndex.Load was missing.)
    [Fact]
    public void SegmentedSymbolIndex_RejectsCorruptTombstoneCount()
    {
        var dir = NewTempDir();
        try
        {
            var idx = SegmentedSymbolIndex.Create(dir);
            idx.Add(new Symbol("Foo", SymbolKind.Class, "a.cs", 1, 1));
            idx.Flush();
            idx.Dispose();

            // Overwrite the tombstone file with a negative segment count.
            File.WriteAllBytes(Path.Combine(dir, "symbols.tombstones"), BitConverter.GetBytes(-1));

            Assert.Throws<InvalidDataException>(() => SegmentedSymbolIndex.Open(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // A forced (no-newline) block cut in a >16 MB line must land on a UTF-8 codepoint boundary, else the split
    // sequence and the next block's leading bytes mis-decode -> corrupt block-Bloom trigrams -> missed hits.
    [Theory]
    [InlineData(new byte[] { 0x61, 0x62, 0x63 }, 3)]              // "abc" - complete ASCII, no trim
    [InlineData(new byte[] { 0x61, 0xC3, 0xA9 }, 3)]             // "aé" - complete 2-byte, no trim
    [InlineData(new byte[] { 0x61, 0xC3 }, 1)]                    // split 2-byte -> drop the lone lead
    [InlineData(new byte[] { 0x61, 0xE2, 0x82 }, 1)]             // split 3-byte -> drop the partial
    [InlineData(new byte[] { 0x61, 0xE2, 0x82, 0xAC }, 4)]       // complete 3-byte (€), no trim
    [InlineData(new byte[] { 0x61, 0x62, 0xF0, 0x9F, 0x98 }, 2)] // split 4-byte emoji -> drop the partial
    public void TrimToCharBoundary_CutsOnCodepointBoundary(byte[] data, int expected)
    {
        Assert.Equal(expected, LargeFileIndexer.TrimToCharBoundary(data, skip: 0, len: data.Length));
    }

    // FromSegmentFiles must not trust a caller-supplied number below the on-disk max: the next flush would reuse
    // (overwrite) a live segment file, breaking the monotonic never-reuse invariant behind Windows mmap safety.
    [Fact]
    public void FromSegmentFiles_ClampsSegmentNumber_NoLiveSegmentOverwrite()
    {
        var dir = NewTempDir();
        try
        {
            var idx = SegmentedIndex.Create("root", dir);
            idx.AddDocumentText("a.cs", "alpha beta gamma");
            idx.Flush();                                  // writes seg-00000000.ccseg
            idx.Dispose();
            var first = SegmentedIndex.SegmentFileName(0);
            Assert.True(File.Exists(Path.Combine(dir, first)));

            // A caller hands back a STALE low number (0) though seg-00000000 already exists.
            var asm = SegmentedIndex.FromSegmentFiles("root", dir, new[] { first }, nextSegmentNumber: 0);
            asm.AddDocumentText("b.cs", "delta epsilon");
            asm.Flush();                                  // must NOT overwrite seg-00000000
            asm.Dispose();

            Assert.True(File.Exists(Path.Combine(dir, first)), "the original live segment must not be overwritten");
            Assert.True(File.Exists(Path.Combine(dir, SegmentedIndex.SegmentFileName(1))), "the new segment must get a fresh number");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Durability net: if the manifest goes missing but segment files survive (a non-atomic replace that lost the
    // manifest over a share), Open must reconstruct from disk rather than treat the index as empty.
    [Fact]
    public void Open_ManifestMissing_ReconstructsFromSegmentFilesOnDisk()
    {
        var dir = NewTempDir();
        try
        {
            var idx = SegmentedIndex.Create("root", dir);
            idx.AddDocumentText("a.cs", "needle in a haystack");
            idx.AddDocumentText("b.cs", "another document here");
            idx.Flush();
            int docs = idx.DocumentCount;
            idx.Dispose();
            Assert.True(docs >= 2);

            File.Delete(Path.Combine(dir, "segments.manifest")); // simulate the lost-manifest failure

            using var reopened = SegmentedIndex.Open("root", dir);
            Assert.True(reopened.SegmentCount >= 1, "segments should be reconstructed from disk");
            Assert.Equal(docs, reopened.DocumentCount); // no data lost - reads via the reconstructed segments
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
