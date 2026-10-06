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
    /// Baseline was 0 = "the index output as it has shipped": an existing index with no stamp reads back as 0
    /// and therefore equals baseline, so adding this mechanism raised NO false alarm on already-built indexes.
    /// BUMP THIS (and the tripwire test in IndexerContentVersionTests) the next time indexer output changes.
    /// It cannot retroactively flag pre-stamp indexes (we never recorded what built them) - it is a
    /// going-forward guarantee. Incremental-update drift is a DIFFERENT problem and is not covered here.
    ///
    /// History:
    ///   1 - mid-size files (>= LargeFileIndexer.SidecarThresholdBytes, under the streaming threshold) now get
    ///       a positional block sidecar, so a rebuild adds those sidecars and makes their search block-selective.
    ///   2 - C# records (and positional record properties) are now extracted as symbols; v1.0.232 also changed
    ///       output (seam window in forced-cut block Blooms, symlinked files no longer indexed). A rebuild picks up
    ///       records find_definition/search_symbols otherwise miss until each file is next touched.
    ///   3 - C++ member functions defined inside a class body, and out-of-class definitions (Value::method), are now
    ///       symbols: find_definition finds them, and find_references (a C/C++ name search) stops listing them as uses.
    ///   4 - C/C++ symbols are DEFINITIONS only: a struct/union/enum/class counts only with a body (not every `struct x`
    ///       use), a plain function only with a return type (not `list_for_each(...) {`), plus pointer/reference-returning
    ///       functions, ns::C::m, typedefs/aliases in C++ headers, and .inl/.ipp/.tcc/.h++/.c++ files. v3 indexes record
    ///       type USES as definitions, which made find_references drop real uses. Also new in v4: constructors in a class
    ///       body, destructors (~A, A::~A), operators (operator=), pointer and function-pointer typedefs, functions that
    ///       return a function pointer, and export-macro types (class LIB_API K {...}).
    /// </summary>
    public const int IndexerContentVersion = 4;
}
