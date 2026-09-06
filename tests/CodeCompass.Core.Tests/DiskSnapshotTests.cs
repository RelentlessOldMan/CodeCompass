using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodeCompass.Core.Changes;
using Xunit;

namespace CodeCompass.Core.Tests;

public class DiskSnapshotTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static FileState St(int seed) =>
        new(seed * 10L, seed * 100L, Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(seed))));

    private static void AssertMatches(DiskSnapshot snap, IReadOnlyDictionary<string, FileState> model)
    {
        Assert.Equal(model.Count, snap.Count);
        Assert.Equal(model.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     snap.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (k, v) in model)
        {
            Assert.True(snap.TryGetValue(k, out var got), $"missing {k}");
            Assert.Equal(v, got);
        }
        Assert.False(snap.TryGetValue("definitely/not/here.xyz", out _));
    }

    [Fact]
    public void FullBase_Roundtrips()
    {
        var dir = NewTempDir();
        try
        {
            var model = new Dictionary<string, FileState>(StringComparer.Ordinal)
            {
                ["src/a.cs"] = St(1),
                ["src/b.cs"] = St(2),
                ["z/deep/c.txt"] = St(3),
            };
            DiskSnapshot.WriteFullBase(dir, model);

            using var snap = DiskSnapshot.Open(dir);
            AssertMatches(snap, model);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Journal_PersistsOverlayAcrossReopen_WithoutCompaction()
    {
        var dir = NewTempDir();
        try
        {
            using (var snap = DiskSnapshot.Open(dir))
            {
                snap["a.cs"] = St(1);
                snap["b.cs"] = St(2);
                snap.Save(); // base was null -> compacts to a base
                snap["b.cs"] = St(22); // update
                snap["c.cs"] = St(3);  // add
                snap.Save();           // small -> journal only
            }
            using var re = DiskSnapshot.Open(dir);
            Assert.True(re.TryGetValue("b.cs", out var b));
            Assert.Equal(St(22), b);
            Assert.True(re.TryGetValue("c.cs", out _));
            Assert.Equal(3, re.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Tombstone_SurvivesReopenAndCompaction()
    {
        var dir = NewTempDir();
        try
        {
            using (var snap = DiskSnapshot.Open(dir))
            {
                snap["keep.cs"] = St(1);
                snap["gone.cs"] = St(2);
                snap.Save();     // compact to base (both present)
                snap.Remove("gone.cs");
                snap.Save();     // journal with tombstone
            }
            using (var re = DiskSnapshot.Open(dir))
            {
                Assert.False(re.TryGetValue("gone.cs", out _));
                Assert.True(re.TryGetValue("keep.cs", out _));
                re.Compact();    // fold tombstone into a fresh base
            }
            using var re2 = DiskSnapshot.Open(dir);
            Assert.False(re2.TryGetValue("gone.cs", out _));
            Assert.True(re2.TryGetValue("keep.cs", out _));
            Assert.Equal(1, re2.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void KeysWithPrefix_MergesBaseOverlayAndTombstones()
    {
        var dir = NewTempDir();
        try
        {
            using var snap = DiskSnapshot.Open(dir);
            snap["src/a.cs"] = St(1);
            snap["src/sub/b.cs"] = St(2);
            snap["other/c.cs"] = St(3);
            snap.Compact();                 // all in base now
            snap["src/d.cs"] = St(4);       // overlay add under prefix
            snap.Remove("src/a.cs");        // tombstone under prefix

            var underSrc = snap.KeysWithPrefix("src/").OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "src/d.cs", "src/sub/b.cs" }, underSrc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AutoCompaction_TriggersAtThresholdAndBoundsOverlay()
    {
        var dir = NewTempDir();
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_SNAPSHOT_COMPACT");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_SNAPSHOT_COMPACT", "8");
            using var snap = DiskSnapshot.Open(dir);
            for (int i = 0; i < 50; i++) { snap[$"f{i}.cs"] = St(i); snap.Save(); }

            Assert.True(snap.PendingCount < 8, $"overlay should have compacted, was {snap.PendingCount}");
            Assert.Equal(50, snap.Count);

            // exactly one live base file remains (orphans cleaned)
            var bases = Directory.GetFiles(dir, "snapshot-*.base");
            Assert.Single(bases);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_SNAPSHOT_COMPACT", old);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LegacyBlob_IsMigratedOnOpen()
    {
        var dir = NewTempDir();
        try
        {
            var model = new Dictionary<string, FileState>(StringComparer.Ordinal)
            {
                ["legacy/a.cs"] = St(7),
                ["legacy/b.cs"] = St(8),
            };
            using (var fs = File.Create(Path.Combine(dir, "snapshot.bin")))
                SnapshotStore.Save(fs, model);

            using (var snap = DiskSnapshot.Open(dir))
                AssertMatches(snap, model);

            Assert.False(File.Exists(Path.Combine(dir, "snapshot.bin")), "legacy blob should be removed after migration");
            Assert.True(File.Exists(Path.Combine(dir, "snapshot.manifest")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CorruptJournal_IsDroppedSafely()
    {
        var dir = NewTempDir();
        try
        {
            using (var snap = DiskSnapshot.Open(dir))
            {
                snap["a.cs"] = St(1);
                snap.Save(); // base written
                snap["b.cs"] = St(2);
                snap.Save(); // journal written
            }
            File.WriteAllBytes(Path.Combine(dir, "snapshot.journal"), new byte[] { 1, 2, 3, 4, 5, 6 });

            using var re = DiskSnapshot.Open(dir);
            Assert.True(re.TryGetValue("a.cs", out _));   // base survives
            Assert.False(re.TryGetValue("b.cs", out _));  // corrupt journal dropped
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TryOpen_FalseWhenNothingExists()
    {
        var dir = NewTempDir();
        try { Assert.False(DiskSnapshot.TryOpen(dir, out _)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RandomOps_MatchReferenceDictionary_AcrossReopens()
    {
        var dir = NewTempDir();
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_SNAPSHOT_COMPACT");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_SNAPSHOT_COMPACT", "16"); // frequent compaction
            var model = new Dictionary<string, FileState>(StringComparer.Ordinal);
            var rand = new Random(1234);
            var paths = Enumerable.Range(0, 60).Select(i => $"dir{i % 7}/file{i}.cs").ToArray();

            for (int round = 0; round < 12; round++)
            {
                using (var snap = DiskSnapshot.Open(dir))
                {
                    int ops = rand.Next(1, 15);
                    for (int o = 0; o < ops; o++)
                    {
                        var p = paths[rand.Next(paths.Length)];
                        if (rand.Next(3) == 0) { snap.Remove(p); model.Remove(p); }
                        else { var s = St(rand.Next(1, 1000)); snap[p] = s; model[p] = s; }
                    }
                    snap.Save();
                }
                using var check = DiskSnapshot.Open(dir);
                AssertMatches(check, model);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_SNAPSHOT_COMPACT", old);
            Directory.Delete(dir, true);
        }
    }
}
