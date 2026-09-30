using CodeCompass.Core.Config;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Indexing;

/// <summary>What indexing would do to a repo under the current (env + config) caps: how much is
/// indexed, and which files fall outside a cap and are therefore invisible to symbol search or to
/// search entirely. A diagnostic to help an operator decide whether to raise a cap - never changes
/// anything.
///
/// It is a PRE-INDEX ESTIMATE from directory metadata (name + size), NOT a census of a built index,
/// so its file count is EXPECTED to differ from `codecompass index` / `link list` and that is not a
/// discrepancy: (1) survey decides purely from the directory listing and does not open files, so it
/// cannot apply the index's content-based binary drop (a NUL-sniff on each file's first bytes) - its
/// count is therefore an upper bound; (2) on a live/VCS-backed repo the tree changes between a survey
/// and a build. The walk itself is shared with the real indexer (<see cref="FileWalker"/>), so
/// ignored-dir / reparse-point / size-cap / network-retry decisions can never drift from a build.</summary>
public sealed record SurveyReport(
    int IndexedFiles,
    long IndexedBytes,
    long MaxSymbolBytes,
    long MaxFileBytes,
    IReadOnlyList<(string Path, long Bytes)> SymbolSkipped, // has a language + over the symbol cap (text-searchable, no symbols)
    IReadOnlyList<(string Path, long Bytes)> OverFileCap);  // over the file cap (absent from the index entirely)

public static class Surveyor
{
    /// <summary>Walk the repo via the shared <see cref="FileWalker"/> (so ignored-dir / reparse / size-cap /
    /// network-retry decisions match a real build exactly) and bucket files against the size caps. Reads
    /// <c>.codecompass.json</c> so the report reflects the caps a real index would use.</summary>
    public static SurveyReport Survey(string root)
    {
        root = Path.GetFullPath(root);
        CodeCompassConfig.Load(root);
        long symCap = CodeCompassConfig.MaxSymbolChars();
        long fileCap = CodeCompassConfig.MaxFileBytes();

        // Same walk the indexer uses. new IgnoreRules().MaxFileSizeBytes == CodeCompassConfig.MaxFileBytes(),
        // so the file-cap split here is identical to the build's - no second, drift-prone traversal.
        var walker = new FileWalker(new IgnoreRules()) { CollectOverCapFiles = true };

        int indexed = 0;
        long indexedBytes = 0;
        var symbolSkipped = new List<(string, long)>();

        foreach (var f in walker.Walk(root))
        {
            // FileWalker yields exactly the under-cap, non-ignored files a build would index (content-based
            // binary drop still happens at index time; see the type doc - survey's count is an upper bound).
            indexed++;
            indexedBytes += f.Size;
            if (f.Size > symCap && LanguageRegistry.ForPath(f.RelativePath) is not null)
                symbolSkipped.Add((f.RelativePath, f.Size));
        }

        // Over-file-cap files come off the same walk (the walker collected them because CollectOverCapFiles).
        var overFileCap = new List<(string, long)>(walker.OverCapFiles);

        symbolSkipped.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        overFileCap.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return new SurveyReport(indexed, indexedBytes, symCap, fileCap, symbolSkipped, overFileCap);
    }
}
