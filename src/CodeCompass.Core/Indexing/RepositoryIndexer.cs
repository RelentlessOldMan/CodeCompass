using System.Diagnostics;
using System.Text;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Indexing;

public sealed record IndexStats(int Files, long Bytes, int Trigrams, int Symbols, double Seconds, long IndexBytes);

/// <summary>
/// Builds and loads a repository's on-disk indexes (trigram text + symbols). Shared by
/// the CLI and the MCP server so index layout and behavior stay identical.
/// </summary>
public static class RepositoryIndexer
{
    public static (TrigramIndex Text, SymbolIndex Symbols, IndexStats Stats) Build(string root)
    {
        root = Path.GetFullPath(root);
        var walker = new FileWalker(new IgnoreRules());
        var sw = Stopwatch.StartNew();

        long totalBytes = 0;
        var docs = new List<(string relPath, string fullPath)>();
        foreach (var f in walker.Walk(root))
        {
            docs.Add((f.RelativePath, f.FullPath));
            totalBytes += f.Size;
        }

        var text = TrigramIndex.Build(root, docs);

        using var extractor = new TreeSitterSymbolExtractor();
        var symbols = new List<Symbol>();
        foreach (var (rel, full) in docs)
        {
            if (LanguageRegistry.ForPath(rel) is null) continue;
            string content;
            try
            {
                var bytes = File.ReadAllBytes(full);
                if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;
                content = Encoding.UTF8.GetString(bytes);
            }
            catch { continue; }
            symbols.AddRange(extractor.Extract(rel, content));
        }
        var symbolIndex = SymbolIndex.Build(symbols);
        sw.Stop();

        var indexPath = IndexStore.IndexPath(root);
        using (var fs = File.Create(indexPath)) text.Save(fs);
        using (var fs = File.Create(IndexStore.SymbolIndexPath(root))) symbolIndex.Save(fs);

        long indexBytes = new FileInfo(indexPath).Length;
        var stats = new IndexStats(text.DocumentCount, totalBytes, text.TrigramCount,
                                   symbolIndex.Count, sw.Elapsed.TotalSeconds, indexBytes);
        return (text, symbolIndex, stats);
    }

    public static bool TryLoad(string root, out TrigramIndex text, out SymbolIndex symbols)
    {
        root = Path.GetFullPath(root);
        var indexPath = IndexStore.IndexPath(root);
        var symbolPath = IndexStore.SymbolIndexPath(root);
        if (!File.Exists(indexPath) || !File.Exists(symbolPath))
        {
            text = null!;
            symbols = null!;
            return false;
        }
        using (var fs = File.OpenRead(indexPath)) text = TrigramIndex.Load(fs);
        using (var fs = File.OpenRead(symbolPath)) symbols = SymbolIndex.Load(fs);
        return true;
    }
}
