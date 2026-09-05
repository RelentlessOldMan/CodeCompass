namespace CodeCompass.Core.Changes;

/// <summary>
/// Per-file state used for change detection. Size and modified-time are the cheap
/// pre-filter (no read); ContentHash is the source of truth that lets us skip
/// touched-but-identical files.
/// </summary>
public readonly record struct FileState(long Size, long MTimeTicks, string ContentHash);
