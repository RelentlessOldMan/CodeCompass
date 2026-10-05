using System;
using System.IO;
using System.Text;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// The crash-safety foundation shared by the workflow (save-safety) and crash-recovery concerns: an atomic
// write must fully replace the target on success, and on FAILURE must leave the previous known-good file
// untouched and no .tmp orphan behind. CodeCompass has no user documents - its "saved data" is the on-disk
// index cache - but the same invariant protects it: a torn/failed segment/manifest/snapshot write must not
// destroy the last valid one. (RobustnessTests covers reading a corrupt cache; this covers WRITING safely.)
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Write_RoundTrips_AndReplacesExistingContent()
    {
        var path = Path.Combine(_dir, "data.bin");
        AtomicFile.Write(path, s => { var b = Encoding.UTF8.GetBytes("FIRST"); s.Write(b, 0, b.Length); });
        Assert.Equal("FIRST", File.ReadAllText(path));

        AtomicFile.Write(path, s => { var b = Encoding.UTF8.GetBytes("SECOND-longer"); s.Write(b, 0, b.Length); });
        Assert.Equal("SECOND-longer", File.ReadAllText(path)); // fully replaced, not appended/truncated
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_WhenBodyThrows_PreservesExistingFile_AndLeavesNoTempOrphan()
    {
        var path = Path.Combine(_dir, "ledger.bin");
        AtomicFile.Write(path, s => { var b = Encoding.UTF8.GetBytes("GOOD"); s.Write(b, 0, b.Length); });

        // A write whose body fails partway (disk full, serialization bug, crash) must NOT replace or truncate
        // the last known-good file - the whole point of the temp+rename dance.
        var ex = Record.Exception(() => AtomicFile.Write(path, s =>
        {
            var b = Encoding.UTF8.GetBytes("PARTIAL-BAD");
            s.Write(b, 0, b.Length);
            throw new InvalidOperationException("simulated mid-write failure");
        }));
        Assert.IsType<InvalidOperationException>(ex);

        Assert.Equal("GOOD", File.ReadAllText(path));                    // previous content intact
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // a failed write must not leave a temp orphan
    }

    [Fact]
    public void WriteText_WhenBodyThrows_PreservesExistingFile_AndLeavesNoTempOrphan()
    {
        var path = Path.Combine(_dir, "manifest.txt");
        AtomicFile.WriteText(path, w => w.Write("v1-good"));

        var ex = Record.Exception(() => AtomicFile.WriteText(path, w =>
        {
            w.Write("v2-partial");
            throw new InvalidOperationException("simulated mid-write failure");
        }));
        Assert.IsType<InvalidOperationException>(ex);

        Assert.Equal("v1-good", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // a failed text write must not leave a temp orphan
    }

    [Fact]
    public void WriteText_RoundTrips()
    {
        var path = Path.Combine(_dir, "cfg.txt");
        AtomicFile.WriteText(path, w => { w.WriteLine("line1"); w.WriteLine("line2"); });
        var lines = File.ReadAllLines(path);
        Assert.Equal(new[] { "line1", "line2" }, lines);
    }

    // The network-safer replace (File.Replace with a null backup, fallback to Move) must, on the happy path,
    // leave exactly the target - no .tmp orphan and no leftover backup file - across repeated replacements.
    [Fact]
    public void Write_RepeatedReplace_LeavesOnlyTheTarget_NoTempOrBackupOrphans()
    {
        var path = Path.Combine(_dir, "reg.bin");
        for (int i = 0; i < 20; i++)
        {
            var payload = Encoding.UTF8.GetBytes($"rev-{i}");
            AtomicFile.Write(path, s => s.Write(payload, 0, payload.Length));
        }
        Assert.Equal("rev-19", File.ReadAllText(path)); // last writer wins, content intact

        var leftovers = Directory.GetFiles(_dir)
            .Select(Path.GetFileName)
            .Where(n => n != "reg.bin")
            .ToArray();
        Assert.Empty(leftovers); // no reg.bin.tmp and no File.Replace backup left behind
    }
}
