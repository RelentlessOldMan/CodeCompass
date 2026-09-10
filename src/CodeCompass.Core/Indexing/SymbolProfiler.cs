using System.Diagnostics;
using CodeCompass.Core.Config;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Text;

namespace CodeCompass.Core.Indexing;

/// <summary>
/// Answers the questions needed to set <c>maxSymbolMb</c> from data on a real repo: how big files get
/// per language, whether the big ones actually yield symbols, and where parse cost starts to hurt.
///
/// Two cost tiers, so this is cheap even on a multi-GB repo:
///  * The file-size distribution per language is gathered from <c>stat</c> only - no parsing - so it
///    is essentially free across millions of files.
///  * tree-sitter is only run to learn symbol yield + parse cost, and by default only on the LARGEST
///    <c>maxParsePerLang</c> files per language (the cap-relevant ones; small files are known-fast and
///    aren't the decision). <c>full</c> parses everything. Parsing is sequential, ascending by size,
///    each parse joined with a timeout (a slow file becomes the language's "knee" and stops its scan),
///    with a hard read ceiling and a global wall-clock budget - so it can neither hang nor crawl.
/// Read-only; changes nothing.
/// </summary>
public sealed record FileParse(string Path, long Bytes, int Symbols, double ParseMs, bool TimedOut);

public sealed record LanguageProfile(
    string Extension,
    string Language,
    // Candidate population - every grammar-eligible file, from stat only (no parse):
    int CandidateFiles,
    long CandidateP50,
    long CandidateP95,
    long CandidateMax,
    string CandidateMaxPath,
    // Parsed sample (the largest files, or all under `full`):
    int FilesParsed,
    int FilesWithSymbols,
    long LargestSymbolBearing,        // largest parsed file that yielded >=1 symbol
    string LargestSymbolBearingPath,
    double SlowestMs,
    long SlowestBytes,
    FileParse? Knee,                  // the file that tripped the timeout / slow-stop (null if none did)
    int NotParsed,                    // eligible files we did NOT parse (sampled out / over ceiling / after knee / budget)
    long NotParsedMaxBytes);

public sealed record SymbolProfileReport(
    long TimeoutMs,
    long HardCeilingBytes,
    int MaxParsePerLang,
    bool Full,
    bool BudgetExhausted,
    IReadOnlyList<LanguageProfile> Languages,
    IReadOnlyList<FileParse> All);

public static class SymbolProfiler
{
    /// <param name="timeoutMs">Per-file parse timeout. A file that exceeds it is recorded as the
    /// language's knee and larger files in that language are skipped (they can only be worse).</param>
    /// <param name="hardCeilingBytes">Never even read files bigger than this (avoids OOM on absurd
    /// files); they are counted as not-parsed.</param>
    /// <param name="maxParsePerLang">Parse at most this many files per language - the largest ones -
    /// unless <paramref name="full"/>. Keeps a huge repo fast; the size distribution still covers all
    /// files (it is stat-only).</param>
    /// <param name="full">Parse every eligible file (small repos / exhaustive runs).</param>
    /// <param name="budgetMs">Global wall-clock budget across all parsing; when exceeded, stop and
    /// report what was covered rather than run unbounded.</param>
    public static SymbolProfileReport Profile(
        string root,
        long timeoutMs = 20_000,
        long hardCeilingBytes = 64L * 1024 * 1024,
        int maxParsePerLang = 100,
        bool full = false,
        long budgetMs = 120_000)
    {
        root = Path.GetFullPath(root);
        CodeCompassConfig.Load(root);
        var ignore = new IgnoreRules();

        // Collect every file that a grammar covers, grouped by extension (so .h and .cpp - same
        // grammar, very different cost - report separately, which is exactly the interesting split).
        // This is stat-only, so it is cheap even for a repo with millions of files.
        var byExt = new Dictionary<string, List<(string Rel, string Full, long Size)>>(StringComparer.OrdinalIgnoreCase);
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
                var name = Path.GetFileName(file);
                if (ignore.IsIgnoredFile(name, 0)) continue;
                if (LanguageRegistry.ForPath(name) is null) continue; // no grammar -> no symbols to profile
                long size;
                try { size = new FileInfo(file).Length; } catch { continue; }
                var ext = Path.GetExtension(name).ToLowerInvariant();
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!byExt.TryGetValue(ext, out var list)) byExt[ext] = list = new();
                list.Add((rel, file, size));
            }
        }

        using var extractor = new TreeSitterSymbolExtractor();
        var profiles = new List<LanguageProfile>();
        var all = new List<FileParse>();
        var budget = Stopwatch.StartNew();
        bool budgetExhausted = false;

        foreach (var (ext, list) in byExt.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            list.Sort((a, b) => a.Size.CompareTo(b.Size)); // ascending: never surprise-hang on the biggest first
            var lang = LanguageRegistry.ForExtension(ext)!.Key;

            // Candidate distribution (free - stat only, whole population).
            var sizes = list.Select(f => f.Size).ToList(); // already ascending
            long candP50 = Percentile(sizes, 0.50), candP95 = Percentile(sizes, 0.95);
            long candMax = sizes.Count > 0 ? sizes[^1] : 0;
            string candMaxPath = list.Count > 0 ? list[^1].Rel : "";

            // Warm the grammar/query load so its one-time cost isn't charged to the first real file.
            try { extractor.ExtractUncapped("warm" + ext, "int x;"); } catch { /* best effort */ }

            // Parse set: the largest maxParsePerLang files (still ascending, for the knee logic), or all.
            int skipSmall = full ? 0 : Math.Max(0, list.Count - maxParsePerLang);
            long notParsedMax = skipSmall > 0 ? list[skipSmall - 1].Size : 0; // largest of the sampled-out small files
            int notParsed = skipSmall;

            var parses = new List<FileParse>();
            FileParse? knee = null;

            for (int i = skipSmall; i < list.Count; i++)
            {
                var (rel, full2, size) = list[i];
                if (knee is not null || budgetExhausted || size > hardCeilingBytes)
                {
                    notParsed++; notParsedMax = Math.Max(notParsedMax, size);
                    continue;
                }
                if (budget.ElapsedMilliseconds > budgetMs)
                {
                    budgetExhausted = true;
                    notParsed++; notParsedMax = Math.Max(notParsedMax, size);
                    continue;
                }

                string text;
                try { text = TextDecoder.FromBytes(File.ReadAllBytes(full2)); }
                catch { notParsed++; notParsedMax = Math.Max(notParsedMax, size); continue; }

                var (ms, count, timedOut) = TimeParse(extractor, rel, text, timeoutMs);
                var fp = new FileParse(rel, size, count, ms, timedOut);
                parses.Add(fp);
                all.Add(fp);

                // A timeout, or a completed-but-alarmingly-slow parse, marks the knee: bigger files in
                // this language can only be worse, so stop here rather than risk a real hang.
                if (timedOut || ms >= timeoutMs / 2.0) knee = fp;
            }

            var withSym = parses.Where(p => p.Symbols > 0).ToList();
            var slowest = parses.Count > 0 ? parses.OrderByDescending(p => p.ParseMs).First() : null;
            var largestSym = withSym.Count > 0 ? withSym.OrderByDescending(p => p.Bytes).First() : null;

            profiles.Add(new LanguageProfile(
                ext, lang,
                list.Count, candP50, candP95, candMax, candMaxPath,
                parses.Count,
                withSym.Count,
                largestSym?.Bytes ?? 0,
                largestSym?.Path ?? "",
                slowest?.ParseMs ?? 0,
                slowest?.Bytes ?? 0,
                knee,
                notParsed,
                notParsedMax));
        }

        profiles.Sort((a, b) => b.CandidateMax.CompareTo(a.CandidateMax));
        return new SymbolProfileReport(timeoutMs, hardCeilingBytes, maxParsePerLang, full, budgetExhausted, profiles, all);
    }

    // Run one parse on a worker thread and join with a timeout. tree-sitter is native and can't be
    // aborted mid-parse, so on timeout we leave the worker to finish on its own and return; because
    // the caller stops the language's scan at the first timeout, at most one such thread ever lingers.
    private static (double Ms, int Symbols, bool TimedOut) TimeParse(
        TreeSitterSymbolExtractor extractor, string rel, string text, long timeoutMs)
    {
        int symbols = 0;
        var sw = Stopwatch.StartNew();
        var t = new Thread(() =>
        {
            try { symbols = extractor.ExtractUncapped(rel, text).Count; } catch { symbols = -1; }
        }) { IsBackground = true };
        t.Start();
        bool finished = t.Join((int)Math.Min(timeoutMs, int.MaxValue));
        sw.Stop();
        return finished ? (sw.Elapsed.TotalMilliseconds, symbols, false) : (timeoutMs, -1, true);
    }

    // Nearest-rank percentile on an ascending list (empty -> 0).
    private static long Percentile(IReadOnlyList<long> ascending, double p)
    {
        if (ascending.Count == 0) return 0;
        int idx = (int)Math.Ceiling(p * ascending.Count) - 1;
        return ascending[Math.Clamp(idx, 0, ascending.Count - 1)];
    }
}
