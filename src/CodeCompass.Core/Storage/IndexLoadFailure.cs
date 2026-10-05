namespace CodeCompass.Core.Storage;

/// <summary>The on-disk index was written by a NEWER CodeCompass (a higher segment format version) - e.g. the Claude
/// Code plugin updated while a Codex install on the same machine didn't. Distinct from corruption: rebuilding over it
/// would make the two installs rebuild each other's index forever, so callers refuse and say "upgrade" instead.</summary>
public sealed class IndexFormatTooNewException : Exception
{
    public IndexFormatTooNewException(string what, int found, int supported)
        : base($"{what} format v{found} is newer than this CodeCompass supports (v{supported}) - upgrade this install") { }
}

/// <summary>Why <c>RepositoryIndexer.TryLoad</c> failed - each calls for a different response.</summary>
public enum IndexLoadFailure
{
    None,
    /// <summary>No index exists: build one.</summary>
    Missing,
    /// <summary>Structurally invalid: rebuild.</summary>
    Corrupt,
    /// <summary>A read failed transiently (sharing violation, share hiccup): retry later - do NOT rebuild.</summary>
    Transient,
    /// <summary>Written by a newer CodeCompass: refuse to rebuild over it; ask for an upgrade.</summary>
    NewerFormat,
}