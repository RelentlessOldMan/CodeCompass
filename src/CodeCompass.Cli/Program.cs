using System.Diagnostics;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Walking;

return args.Length == 0
    ? Usage()
    : args[0].ToLowerInvariant() switch
    {
        "index" => CmdIndex(args),
        "search" => CmdSearch(args),
        _ => Usage(),
    };

static int Usage()
{
    Console.Error.WriteLine("CodeCompass (Phase 1)");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  codecompass index  <path>");
    Console.Error.WriteLine("  codecompass search <path> <query>");
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
    sw.Stop();

    var path = IndexStore.IndexPath(root);
    using (var fs = File.Create(path)) index.Save(fs);
    long indexSize = new FileInfo(path).Length;

    double mb = totalBytes / (1024.0 * 1024.0);
    double secs = sw.Elapsed.TotalSeconds;
    double throughput = secs > 0 ? mb / secs : 0;
    double ratio = totalBytes > 0 ? (double)indexSize / totalBytes : 0;

    Console.WriteLine($"Indexed {index.DocumentCount:N0} files ({mb:F1} MB) in {secs:F2}s  ({throughput:F1} MB/s)");
    Console.WriteLine($"Trigrams: {index.TrigramCount:N0}   Index: {indexSize / (1024.0 * 1024.0):F1} MB ({ratio:F2}x corpus)");
    Console.WriteLine($"Stored:   {path}");
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
