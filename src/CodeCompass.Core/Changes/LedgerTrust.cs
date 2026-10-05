namespace CodeCompass.Core.Changes;

/// <summary>
/// Decides, at the moment a file's hash is RECORDED, whether its size + modified time may later stand in for its content.
/// An update skips a file whose size and mtime match the ledger, so a recorded mtime is a promise: "these bytes".
///
/// <para>Two ways that promise can be wrong, both fixed here without comparing clocks or depending on when the ledger file
/// was last written:</para>
/// <list type="bullet">
///   <item><b>Racily clean.</b> On a filesystem with COARSE timestamps (FAT/exFAT 2 s, some NAS boxes 1 s) an edit in the
///   same tick as the read leaves size + mtime unchanged. A file's own timestamp shows its precision: a sub-second part means
///   fine-grained (NTFS 100 ns, ext4/xfs nanoseconds behind Samba), where a later edit always gets a new mtime. A whole-second
///   mtime may be coarse, so if it's recent we record 0 ("unknown"), and the next update re-reads it.</item>
///   <item><b>Changed while being read.</b> A recent file is stat'ed again after its bytes were read; if the size or mtime
///   moved since it was listed, the hash may not match either, so we record 0.</item>
/// </list>
/// <para>"Recent" is within <see cref="RecentWindow"/> of our clock. A file untouched for longer isn't being written now, so
/// it's trusted without the extra stat (no added round trip per file over a share), and the window is wide enough to absorb
/// any realistic clock difference between a file server and this machine. Recording 0 never loses data: it only costs one
/// re-read on a later update. Readers of the ledger see 0 as "doesn't match any file" (see docs/hash-ledger-format.md).</para>
/// </summary>
public static class LedgerTrust
{
    /// <summary>How recently a file must have been modified for its timestamp to need checking.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    /// <summary>The mtime to record for a file just hashed: <paramref name="mtimeListed"/> when it can be trusted, else 0.
    /// <paramref name="sizeListed"/>/<paramref name="mtimeListed"/> are what the file looked like when it was listed (before
    /// its bytes were read).</summary>
    public static long RecordedMTime(string fullPath, long sizeListed, long mtimeListed, long nowUtcTicks)
    {
        if (mtimeListed <= 0) return 0;
        if (mtimeListed < nowUtcTicks - RecentWindow.Ticks) return mtimeListed;   // old: not being written now
        if (mtimeListed % TimeSpan.TicksPerSecond == 0) return 0;                 // recent + coarse: possibly racy
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists || fi.Length != sizeListed || fi.LastWriteTimeUtc.Ticks != mtimeListed) return 0; // changed meanwhile
        }
        catch { return 0; }
        return mtimeListed;
    }

    /// <summary>The state to record for a file just hashed (see <see cref="RecordedMTime"/>).</summary>
    public static FileState Record(string fullPath, long sizeListed, long mtimeListed, long recordedSize, string hash) =>
        new(recordedSize, RecordedMTime(fullPath, sizeListed, mtimeListed, DateTime.UtcNow.Ticks), hash);
}
