using System.Diagnostics;
using System.Text;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Walking;

return args.Length == 0
    ? Usage()
    : args[0].ToLowerInvariant() switch
    {
        "index" => CmdIndex(args),
        "search" => CmdSearch(args),
        "def" => CmdDef(args),
        "symbols" => CmdSymbols(args),
        _ => Usage(),
    };

static int Usage()
{
    Console.Error.WriteLine("CodeCompass (Phase 2)");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  codecompass index   <path>");
    Console.Error.WriteLine("  codecompass search  <path> <query>       literal text search");
    Console.Error.WriteLine("  codecompass def     <path> <name>        exact symbol definition(s)");
    Console.Error.WriteLine("  codecompass symbols <path> <substring>   symbol name search");
    return 1;
}

static int CmdIndex(string[] args)
{
    if (args.Length < 2) return Usage();
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"not a directory: {root}");
        return 1;
    }

    var walker = new FileWalker(new IgnoreRules());

    var sw = Stopwatch.StartNew();
    long totalBytes = 0;
    var docs = new List<(string relPath, string fullPath)>();
    foreach (var f in walker.Walk(root))
    {
        docs.Add((f.RelativePath, f.FullPath));
        totalBytes += f.Size;
    }

    var index = TrigramIndex.Build(root, docs);

    // Symbol extraction pass (only files with a known grammar).
    using var extractor = new TreeSitterSymbolExtractor();
    var symbols = new List<Symbol>();
    foreach (var (rel, full) in docs)
    {
        if (LanguageRegistry.ForPath(rel) is null) continue;
        string text;
        try
        {
            var bytes = File.ReadAllBytes(full);
            if (IgnoreRules.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;
            text = Encoding.UTF8.GetString(bytes);
        }
        catch { continue; }
        symbols.AddRange(extractor.Extract(rel, text));
    }
    var symbolIndex = SymbolIndex.Build(symbols);
    sw.Stop();

    var indexPath = IndexStore.IndexPath(root);
    using (var fs = File.Create(indexPath)) index.Save(fs);
    var symbolPath = IndexStore.SymbolIndexPath(root);
    using (var fs = File.Create(symbolPath)) symbolIndex.Save(fs);

    long indexSize = new FileInfo(indexPath).Length;
    double mb = totalBytes / (1024.0 * 1024.0);
    double secs = sw.Elapsed.TotalSeconds;
    double throughput = secs > 0 ? mb / secs : 0;
    double ratio = totalBytes > 0 ? (double)indexSize / totalBytes : 0;

    Console.WriteLine($"Indexed {index.DocumentCount:N0} files ({mb:F1} MB) in {secs:F2}s  ({throughput:F1} MB/s)");
    Console.WriteLine($"Trigrams: {index.TrigramCount:N0}   Symbols: {symbolIndex.Count:N0}");
    Console.WriteLine($"Text index: {indexSize / (1024.0 * 1024.0):F1} MB ({ratio:F2}x corpus)");
    Console.WriteLine($"Stored:     {IndexStore.GetCacheDir(root)}");
    return 0;
}

static int CmdSearch(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var query = string.Join(' ', args.Skip(2));

    var path = IndexStore.IndexPath(root);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"no index for {root}");
        Console.Error.WriteLine($"run: codecompass index \"{root}\"");
        return 1;
    }

    TrigramIndex index;
    using (var fs = File.OpenRead(path)) index = TrigramIndex.Load(fs);

    var sw = Stopwatch.StartNew();
    var matches = index.Search(query);
    sw.Stop();

    foreach (var m in matches)
        Console.WriteLine($"{m.Path}:{m.Line}:{m.Column}: {m.LineText}");

    Console.Error.WriteLine($"-- {matches.Count} match(es) in {sw.Elapsed.TotalMilliseconds:F0} ms");
    return 0;
}

static int CmdDef(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var name = args[2];

    if (!TryLoadSymbols(root, out var symbols)) return 1;

    var matches = symbols.FindByName(name);
    foreach (var s in matches)
        Console.WriteLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.Kind} {s.Name}");
    Console.Error.WriteLine($"-- {matches.Count} definition(s)");
    return 0;
}

static int CmdSymbols(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var query = args[2];

    if (!TryLoadSymbols(root, out var symbols)) return 1;

    var matches = symbols.Find(query);
    foreach (var s in matches)
        Console.WriteLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.Kind} {s.Name}");
    Console.Error.WriteLine($"-- {matches.Count} symbol(s)");
    return 0;
}

static bool TryLoadSymbols(string root, out SymbolIndex symbols)
{
    var path = IndexStore.SymbolIndexPath(root);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"no symbol index for {root}");
        Console.Error.WriteLine($"run: codecompass index \"{root}\"");
        symbols = new SymbolIndex();
        return false;
    }
    using var fs = File.OpenRead(path);
    symbols = SymbolIndex.Load(fs);
    return true;
}
