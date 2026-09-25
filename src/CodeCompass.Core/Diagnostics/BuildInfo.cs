using System.Reflection;

namespace CodeCompass.Core.Diagnostics;

/// <summary>The build's version string (e.g. <c>1.0.123+a1b2c3d4</c>), derived from git at build
/// time via Directory.Build.props. Logged at startup and reported by <c>codecompass version</c> so a
/// user's reported version pins the exact commit.</summary>
public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// Version of the indexer's OUTPUT-affecting logic - bumped by hand ONLY when a rebuild with the current
    /// binary would produce a materially different index than a prior build would have (tokenizer/trigram
    /// rules, walker file-selection + ignore defaults, tree-sitter grammars/queries, default size caps,
    /// language detection, on-disk segment layout). This is DELIBERATELY separate from <see cref="Version"/>,
    /// which is git-derived and changes every commit: most releases don't touch indexing, so keying "your
    /// index is stale, rebuild" on the product version would cry wolf on every upgrade. Staleness is judged
    /// on THIS number instead, so the nudge fires only when a rebuild would actually change results.
    ///
    /// Baseline is 0 = "the index output as it has shipped": an existing index with no stamp reads back as 0
    /// and therefore equals current, so adding this mechanism raises NO false alarm on already-built indexes.
    /// BUMP THIS (and the tripwire test in IndexerContentVersionTests) the next time indexer output changes.
    /// It cannot retroactively flag pre-stamp indexes (we never recorded what built them) - it is a
    /// going-forward guarantee. Incremental-update drift is a DIFFERENT problem and is not covered here.
    /// </summary>
    public const int IndexerContentVersion = 0;
}
