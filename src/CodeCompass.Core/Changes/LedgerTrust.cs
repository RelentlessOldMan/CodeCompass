namespace CodeCompass.Core.Changes;

/// <summary>One read of a file, for <see cref="LedgerTrust.Record"/>: what the directory listing said, the read handle's
/// identity just before and just after the read (default = unknown), when the read started, and when its result is being
/// recorded (both our clock).</summary>
public readonly record struct ReadStamp(long ListedSize, long ListedMTimeTicks, Storage.FileIdentity Before, Storage.FileIdentity After,
                                        long ReadStartUtcTicks, long RecordedAtUtcTicks);

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
///
/// <para>Ledger v2 adds the read handle's change time and file id: the first look takes the newer of the modified and
/// change times (setting the modified time back moves the change time), the second look also needs both unchanged, and a
/// file whose handle stats moved during the read (or disagree with the listing) is recorded as unknown.</para>
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
                                     FileState? prior = null, long priorRecordedBy = 0, long recordedSize = 0) =>
        Decide(mtimeListed, 0, 0, readStartUtcTicks, network, prior, priorRecordedBy, recordedSize);

    // The signed mtime to record. The first look takes the NEWER of the modified and change times (a change time moves
    // when the modified time is set back, so a "restored timestamp" edit isn't settled). The second look needs the same
    // size, stamps and file id as the pending entry, and is anchored on that entry's own recorded time when it has one
    // (ledger v2), else on the caller's bound for the whole ledger.
    private static long Decide(long mtime, long ctime, long fileId, long readStart, bool network,
                               FileState? prior, long priorRecordedBy, long recordedSize, bool entryClockIsOurs = true)
    {
        if (mtime <= 0) return 0;
        long margin = (network ? NetworkMargin : LocalMargin).Ticks;
        if (Math.Max(mtime, ctime) <= readStart - margin) return mtime;
        if (prior is { } p && p.MTimeTicks == -mtime && p.Size == recordedSize && p.ChangeTicks == ctime
            && (p.FileId == 0 || fileId == 0 || p.FileId == fileId))
        {
            long anchor = entryClockIsOurs && p.HashedAtTicks > 0 ? p.HashedAtTicks : priorRecordedBy;
            if (anchor > 0 && readStart >= anchor + SettleMargin.Ticks) return mtime;
        }
        return -mtime;
    }

    /// <summary>The state to record for a file just hashed, given what was listed and the read handle's identity just
    /// before and after the read (<see cref="ReadStamp"/>). If the handle saw a different file than the listing, or
    /// anything moved during the read, nothing is vouched for: mtime 0 and no change time / file id. With no handle
    /// identity (off Windows) the modified-time rule applies alone. Trusted entries carry HashedAt = the read start;
    /// pending ones carry when they were recorded, which a later second look measures from - unless
    /// <paramref name="entryClockIsOurs"/> is false (the ledger lives on a share, so another machine may have recorded the
    /// entry on its own clock): then only <paramref name="priorRecordedBy"/>, this process's own bound, counts.</summary>
    public static FileState Record(ReadStamp read, bool network, FileState? prior, long priorRecordedBy, long recordedSize, string hash,
                                   bool entryClockIsOurs = true)
    {
        var b = read.Before;
        long ctime = 0, fileId = 0;
        if (b.Known)
        {
            bool stable = b.Size == read.ListedSize && b.MTimeTicks == read.ListedMTimeTicks && b.Size == recordedSize
                          && read.After == b;
            if (!stable) return new FileState(recordedSize, 0, hash, 0, 0, read.ReadStartUtcTicks);
            ctime = b.ChangeTicks;
            fileId = b.FileId;
        }
        long m = Decide(read.ListedMTimeTicks, ctime, fileId, read.ReadStartUtcTicks, network, prior, priorRecordedBy, recordedSize,
                        entryClockIsOurs);
        return new FileState(recordedSize, m, hash, ctime, fileId, m < 0 ? read.RecordedAtUtcTicks : read.ReadStartUtcTicks);
    }
}
