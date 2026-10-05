namespace CodeCompass.Core.Changes;

/// <summary>
/// Per-file state used for change detection. Size and modified-time are the cheap
/// pre-filter (no read); ContentHash is the source of truth that lets us skip
/// touched-but-identical files.
/// </summary>
public readonly record struct FileState(long Size, long MTimeTicks, string ContentHash)
{
    /// <summary>ContentHash recorded for a file that was examined and found to be BINARY (not indexed). Without a
    /// ledger entry every update re-read such a file in full - over a share, forever - just to re-discover it's
    /// binary; with one, the size+mtime prefilter skips it until it actually changes.</summary>
    public const string BinaryHash = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    public bool IsBinary => ContentHash == BinaryHash;
}
