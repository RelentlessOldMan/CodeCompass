namespace CodeCompass.Core.Changes;

/// <summary>
/// Decides, at the moment a file's hash is RECORDED, whether its size + modified time may later stand in for its content.
/// An update skips a file whose size and mtime match the ledger, so a recorded mtime is a promise: "these bytes".
///
/// <para>The promise fails for a same-size edit in the same timestamp tick as the read ("racily clean"), and every
/// filesystem ticks coarsely somewhere: FAT/exFAT 2 s, some NAS 1 s, and NTFS only advances mtime on a ~1-16 ms timer, so
/// back-to-back rewrites keep one sub-second stamp. Two ways to know the stamp's tick is over before the read:</para>
///
/// <list type="number">
/// <item><b>First look:</b> the file was last modified safely BEFORE the read began - by a margin covering the coarsest
/// local tick, or an hour on a network share, whose clock we can't read (the hour absorbs realistic skew).</item>
/// <item><b>Second look</b> (needs no clock agreement): the stamp was already on the file when we first recorded it, so its
/// tick had started by then and ends within one tick. A later read that begins more than <see cref="SettleMargin"/> after
/// that recording - both times on OUR clock - and still lists the same stamp and size reads bytes that no later write can
/// share that stamp with. This is what eventually trusts a file whose stamp is in the future (a share whose clock runs
/// ahead, a NAS without time sync, a future-dated file), which the first look never does.</item>
/// </list>
///
/// <para>An entry neither look can vouch for is recorded PENDING: its observed mtime negated. That matches no file (live
/// stamps are positive), so the next update re-reads it, and it carries the stamp for the second look. 0 still means
/// unknown. Readers of the ledger trust an entry only when its mtime is positive and matches (docs/hash-ledger-format.md).
/// Nothing here depends on when the ledger itself was last written, except as an upper bound on when a pending entry was
/// recorded, which only ever delays trust.</para>
/// </summary>
public static class LedgerTrust
{
    /// <summary>Margin on a local disk: more than the coarsest local timestamp tick (FAT/exFAT 2 s, rounded).</summary>
    public static readonly TimeSpan LocalMargin = TimeSpan.FromSeconds(3);

    /// <summary>Margin on a network share: covers the coarsest tick plus any realistic server/client clock difference.</summary>
    public static readonly TimeSpan NetworkMargin = TimeSpan.FromHours(1);

    /// <summary>Second look: more than the coarsest tick anywhere (FAT/exFAT 2 s). Both ends are our clock, so no skew.</summary>
    public static readonly TimeSpan SettleMargin = TimeSpan.FromSeconds(3);

    /// <summary>The mtime to record for a file just hashed: <paramref name="mtimeListed"/> when trusted (see the class
    /// remarks), <c>-mtimeListed</c> when pending, 0 when the listed time is unknown.</summary>
    /// <param name="readStartUtcTicks">When this read began (our clock).</param>
    /// <param name="prior">The file's previous ledger entry, if any.</param>
    /// <param name="priorRecordedBy">Our-clock time by which <paramref name="prior"/> had been recorded (an upper bound).</param>
    /// <param name="recordedSize">The size being recorded now.</param>
    public static long RecordedMTime(long mtimeListed, long readStartUtcTicks, bool network,
                                     FileState? prior = null, long priorRecordedBy = 0, long recordedSize = 0)
    {
        if (mtimeListed <= 0) return 0;
        long margin = (network ? NetworkMargin : LocalMargin).Ticks;
        if (mtimeListed <= readStartUtcTicks - margin) return mtimeListed;
        if (prior is { } p && p.MTimeTicks == -mtimeListed && p.Size == recordedSize
            && priorRecordedBy > 0 && readStartUtcTicks >= priorRecordedBy + SettleMargin.Ticks)
            return mtimeListed;
        return -mtimeListed;
    }

    /// <summary>The state to record for a file just hashed (see <see cref="RecordedMTime"/>).</summary>
    public static FileState Record(long mtimeListed, long readStartUtcTicks, bool network, long recordedSize, string hash,
                                   FileState? prior = null, long priorRecordedBy = 0) =>
        new(recordedSize, RecordedMTime(mtimeListed, readStartUtcTicks, network, prior, priorRecordedBy, recordedSize), hash);

    /// <summary>A re-read's state: the second look uses the file's previous entry (see <see cref="RecordedMTime"/>).</summary>
    public static FileState Record(long mtimeListed, long readStartUtcTicks, bool network, FileState? prior, long priorRecordedBy,
                                   long recordedSize, string hash) =>
        Record(mtimeListed, readStartUtcTicks, network, recordedSize, hash, prior, priorRecordedBy);
}
