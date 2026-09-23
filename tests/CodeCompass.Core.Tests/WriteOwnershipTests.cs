using System.IO;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

public class WriteOwnershipTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-own-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void SecondAcquire_IsRefusedWhileFirstIsHeld_ThenGrantedAfterRelease()
    {
        var dir = NewDir();
        try
        {
            var first = WriteOwnership.TryAcquire(dir);
            Assert.NotNull(first);
            Assert.True(first!.IsHeld);

            // A second claim fails while the first is held - exactly one writer.
            Assert.Null(WriteOwnership.TryAcquire(dir));

            // Releasing (== process exit closing the handle) lets the next session claim it.
            first.Dispose();
            var second = WriteOwnership.TryAcquire(dir);
            Assert.NotNull(second);
            second!.Dispose();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LeftoverLockFile_IsInert_NotStale()
    {
        var dir = NewDir();
        try
        {
            // Simulate a prior run that left the lock file on disk (crash/power loss) with NO holder.
            File.WriteAllText(Path.Combine(dir, ".writer.lock"), "");
            // Ownership is tested by exclusive open, not file existence - so it's freely acquirable.
            var claim = WriteOwnership.TryAcquire(dir);
            Assert.NotNull(claim);
            claim!.Dispose();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
