using System.Diagnostics;
using CodeCompass.Core.Changes;
using CodeCompass.Core.Hooks;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Text;
using CodeCompass.Semantics;

return args.Length == 0
    ? Usage()
    : args[0].ToLowerInvariant() switch
    {
        "index" => CmdIndex(args),
        "update" => CmdUpdate(args),
        "search" => CmdSearch(args),
        "def" => CmdDef(args),
        "symbols" => CmdSymbols(args),
        "refs" => CmdRefs(args),
        "watch" => CmdWatch(args),
        "hook-block" => CmdHookBlock(),     // PreToolUse hook: deny Grep/Glob
        "hook-context" => CmdHookContext(), // SessionStart hook: inject guidance
        _ => Usage(),
    };

static int Usage()
{
    Console.Error.WriteLine("CodeCompass (Phase 5)");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  codecompass index   <path>               (re)build the full index");
    Console.Error.WriteLine("  codecompass update  <path>               incremental reindex of changes");
    Console.Error.WriteLine("  codecompass watch   <path>               auto-reindex on file changes");
    Console.Error.WriteLine("  codecompass search  <path> <query>       literal text search");
    Console.Error.WriteLine("  codecompass def     <path> <name>        exact symbol definition(s)");
    Console.Error.WriteLine("  codecompass symbols <path> <substring>   symbol name search");
    Console.Error.WriteLine("  codecompass refs    <path> <name>        references (semantic C#/C++, lexical elsewhere)");
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
    double perCore = s.Cores > 0 ? throughput / s.Cores : throughput;
    double ratio = s.Bytes > 0 ? (double)s.IndexBytes / s.Bytes : 0;

    Console.WriteLine($"Indexed {s.Files:N0} files ({mb:F1} MB) in {s.Seconds:F2}s");
    Console.WriteLine($"Throughput: {throughput:F1} MB/s across {s.Cores} core(s)  ({perCore:F1} MB/s/core)");
    Console.WriteLine($"Trigrams: {s.Trigrams:N0}   Symbols: {s.Symbols:N0}");
    Console.WriteLine($"Text index: {s.IndexBytes / (1024.0 * 1024.0):F1} MB ({ratio:F2}x corpus)");
    return 0;
}

static int CmdUpdate(string[] args)
{
    if (args.Length < 2) return Usage();
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"not a directory: {root}"); return 1; }

    var (idx, _, s) = RepositoryIndexer.Update(root);
    idx.Dispose();
    if (s.FullRebuild)
        Console.WriteLine($"Full rebuild ({s.Added} files) in {s.Seconds:F2}s");
    else
        Console.WriteLine($"Updated in {s.Seconds:F2}s: +{s.Added} added, ~{s.Modified} modified, -{s.Removed} removed");
    return 0;
}

static int CmdWatch(string[] args)
{
    if (args.Length < 2) return Usage();
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"not a directory: {root}"); return 1; }

    SegmentedIndex text;
    SymbolIndex symbols;
    if (RepositoryIndexer.TryLoad(root, out text, out symbols))
    {
        Console.Error.WriteLine("loaded existing index");
    }
    else
    {
        Console.Error.WriteLine("building initial index...");
        var b = RepositoryIndexer.Build(root);
        text = b.Text;
        symbols = b.Symbols;
        Console.Error.WriteLine($"indexed {b.Stats.Files} files");
    }
    var snapshot = RepositoryIndexer.LoadSnapshot(root);

    // Keep the index in memory and apply targeted changes in place (no reopen per batch).
    using var watcher = new RepositoryWatcher(root, batch =>
    {
        if (batch.FullReconcile)
        {
            text.Dispose();
            var b = RepositoryIndexer.Build(root);
            text = b.Text;
            symbols = b.Symbols;
            snapshot = RepositoryIndexer.LoadSnapshot(root);
            Console.Error.WriteLine("reindexed: full rebuild");
        }
        else
        {
            var c = RepositoryIndexer.ApplyChanges(text, symbols, snapshot, root, batch.ChangedFullPaths);
            RepositoryIndexer.Persist(root, text, symbols, snapshot);
            if (c.Added != 0 || c.Modified != 0 || c.Removed != 0)
                Console.Error.WriteLine($"reindexed: +{c.Added} ~{c.Modified} -{c.Removed}");
        }
    });
    watcher.Start();

    Console.Error.WriteLine($"watching {root} - press Ctrl+C to stop");
    using var exit = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
    exit.Wait();
    text.Dispose();
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

static int CmdRefs(string[] args)
{
    if (args.Length < 3) return Usage();
    var root = Path.GetFullPath(args[1]);
    var name = args[2];

    // Precise semantic references (comments/strings excluded). Note: from the CLI these
    // build fresh each run; the MCP server keeps them warm across calls.
    var cs = new RoslynCSharpAnalyzer(root).FindReferences(name);
    foreach (var s in cs)
        Console.WriteLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.LineText}");

    var cpp = new ClangCppAnalyzer(root).FindReferences(name);
    foreach (var s in cpp)
        Console.WriteLine($"{s.RelativePath}:{s.Line}:{s.Column}: {s.LineText}");

    // Lexical whole-word references for languages without a semantic analyzer.
    int lexical = 0;
    if (RepositoryIndexer.TryLoad(root, out var index, out _))
    {
        foreach (var m in index.Search(name, 1000))
        {
            if (SemanticCoverage.IsCovered(m.Path)) continue;
            if (!WordBoundary.IsWholeWord(m.LineText, m.Column - 1, name.Length)) continue;
            Console.WriteLine($"{m.Path}:{m.Line}:{m.Column}: {m.LineText}");
            lexical++;
        }
    }

    Console.Error.WriteLine($"-- {cs.Count} C# + {cpp.Count} C/C++ semantic + {lexical} lexical reference(s)");
    return 0;
}

// PreToolUse hook: block Grep/Glob and redirect the agent to CodeCompass.
// The plugin's matcher already limits this to Grep|Glob, so we always deny when
// enforcement is on. Set CODECOMPASS_ENFORCE=0 to disable (grep fallback).
static int CmdHookBlock()
{
    try { _ = Console.In.ReadToEnd(); } catch { /* consume hook stdin */ }

    var enforce = Environment.GetEnvironmentVariable("CODECOMPASS_ENFORCE");
    if (enforce is "0" || string.Equals(enforce, "off", StringComparison.OrdinalIgnoreCase))
        return 0; // enforcement disabled: let the normal permission flow proceed

    Console.WriteLine(HookPayloads.DenySearch());
    return 0;
}

// SessionStart hook: tell the agent to prefer CodeCompass for search/navigation.
static int CmdHookContext()
{
    try { _ = Console.In.ReadToEnd(); } catch { /* consume hook stdin */ }

    Console.WriteLine(HookPayloads.SessionContext());
    return 0;
}

static int NoIndex(string root)
{
    Console.Error.WriteLine($"no index for {root}");
    Console.Error.WriteLine($"run: codecompass index \"{root}\"");
    return 1;
}
