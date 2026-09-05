using System.Diagnostics;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Symbols;

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
    Console.Error.WriteLine("CodeCompass (Phase 3)");
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

    var (_, _, s) = RepositoryIndexer.Build(root);

    double mb = s.Bytes / (1024.0 * 1024.0);
    double throughput = s.Seconds > 0 ? mb / s.Seconds : 0;
    double ratio = s.Bytes > 0 ? (double)s.IndexBytes / s.Bytes : 0;

    Console.WriteLine($"Indexed {s.Files:N0} files ({mb:F1} MB) in {s.Seconds:F2}s  ({throughput:F1} MB/s)");
    Console.WriteLine($"Trigrams: {s.Trigrams:N0}   Symbols: {s.Symbols:N0}");
    Console.WriteLine($"Text index: {s.IndexBytes / (1024.0 * 1024.0):F1} MB ({ratio:F2}x corpus)");
    return 0;
}

static int CmdSearch(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var query = string.Join(' ', args.Skip(2));

    if (!RepositoryIndexer.TryLoad(root, out var index, out _)) return NoIndex(root);

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

    if (!RepositoryIndexer.TryLoad(root, out _, out var symbols)) return NoIndex(root);

    var matches = symbols.FindByName(name);
    foreach (var symbol in matches)
        Console.WriteLine($"{symbol.RelativePath}:{symbol.Line}:{symbol.Column}: {symbol.Kind} {symbol.Name}");
    Console.Error.WriteLine($"-- {matches.Count} definition(s)");
    return 0;
}

static int CmdSymbols(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var query = args[2];

    if (!RepositoryIndexer.TryLoad(root, out _, out var symbols)) return NoIndex(root);

    var matches = symbols.Find(query);
    foreach (var symbol in matches)
        Console.WriteLine($"{symbol.RelativePath}:{symbol.Line}:{symbol.Column}: {symbol.Kind} {symbol.Name}");
    Console.Error.WriteLine($"-- {matches.Count} symbol(s)");
    return 0;
}

static int NoIndex(string root)
{
    Console.Error.WriteLine($"no index for {root}");
    Console.Error.WriteLine($"run: codecompass index \"{root}\"");
    return 1;
}
