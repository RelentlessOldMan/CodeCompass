using CodeCompass.Core.Config;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Symbols;

namespace CodeCompass.Core.Indexing;

/// <summary>What indexing would do to a repo under the current (env + config) caps: how much is
/// indexed, and which files fall outside a cap and are therefore invisible to symbol search or to
/// search entirely. A diagnostic to help an operator decide whether to raise a cap - never changes
/// anything.</summary>
public sealed record SurveyReport(
    int IndexedFiles,
    long IndexedBytes,
    long MaxSymbolBytes,
    long MaxFileBytes,
    IReadOnlyList<(string Path, long Bytes)> SymbolSkipped, // has a language + over the symbol cap (text-searchable, no symbols)
    IReadOnlyList<(string Path, long Bytes)> OverFileCap);  // over the file cap (absent from the index entirely)

public static class Surveyor
{
    /// <summary>Walk the repo (honoring ignored dirs, binary extensions, and the active config) and
    /// bucket files against the size caps. Reads <c>.codecompass.json</c> so the report reflects the
    /// caps that a real index would use.</summary>
    public static SurveyReport Survey(string root)
    {
        root = Path.GetFullPath(root);
        CodeCompassConfig.Load(root);
        long symCap = CodeCompassConfig.MaxSymbolChars();
        long fileCap = CodeCompassConfig.MaxFileBytes();
        var ignore = new IgnoreRules();

        int indexed = 0;
        long indexedBytes = 0;
        var symbolSkipped = new List<(string, long)>();
        var overFileCap = new List<(string, long)>();

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs, files;
            try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                if (ignore.IsIgnoredDirectory(Path.GetFileName(sub))) continue;
                try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                stack.Push(sub);
            }

            foreach (var file in files)
            {
                long size;
                try { size = new FileInfo(file).Length; } catch { continue; }
                var name = Path.GetFileName(file);
                if (ignore.IsIgnoredFile(name, 0)) continue; // ignored by extension (size 0 -> only the ext test)

                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (size > fileCap)
                {
                    overFileCap.Add((rel, size));
                }
                else
                {
                    indexed++;
                    indexedBytes += size;
                    if (size > symCap && LanguageRegistry.ForPath(rel) is not null)
                        symbolSkipped.Add((rel, size));
                }
            }
        }

        symbolSkipped.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        overFileCap.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return new SurveyReport(indexed, indexedBytes, symCap, fileCap, symbolSkipped, overFileCap);
    }
}
