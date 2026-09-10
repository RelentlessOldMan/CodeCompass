using System.Diagnostics;
using CodeCompass.Core.Config;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Symbols;
using CodeCompass.Core.Text;

namespace CodeCompass.Core.Indexing;

/// <summary>
/// Answers two questions a person needs to set <c>maxSymbolMb</c> sensibly, measured on a real repo:
/// (1) how big do files that actually yield symbols get, per language, and (2) where does tree-sitter's
/// ~O(n^2) parse cost start to hurt. It parses each eligible source file <b>ignoring the symbol cap</b>
/// (that is the point - to see past it), but does so safely: files are visited in ascending size order
/// per language and each parse runs on a worker thread joined with a timeout, so a pathological file is
/// recorded and stops that language's scan rather than hanging the tool. Read-only; changes nothing.
/// </summary>
public sealed record FileParse(string Path, long Bytes, int Symbols, double ParseMs, bool TimedOut);

public sealed record LanguageProfile(
    string Extension,
    string Language,
    int FilesParsed,
    int FilesWithSymbols,
    long SymbolBearingP50,
    long SymbolBearingP95,
    long SymbolBearingMax,
    string SymbolBearingMaxPath,
    double SlowestMs,
    long SlowestBytes,
    FileParse? Knee,          // the file that tripped the timeout / slow-stop (null if none did)
    int LargerNotProfiled,    // files in this language above the knee we deliberately skipped
    long LargerMaxBytes);

public sealed record SymbolProfileReport(
    long TimeoutMs,
    long HardCeilingBytes,
    IReadOnlyList<LanguageProfile> Languages,
    IReadOnlyList<FileParse> All);

public static class SymbolProfiler
{
    /// <param name="timeoutMs">Per-file parse timeout. A file that exceeds it is recorded as the
    /// language's knee and larger files in that language are skipped (they can only be worse).</param>
    /// <param name="hardCeilingBytes">Never even read files bigger than this (avoids OOM on absurd
    /// files); they are counted as "not profiled".</param>
    public static SymbolProfileReport Profile(string root, long timeoutMs = 20_000, long hardCeilingBytes = 64L * 1024 * 1024)
    {
        root = Path.GetFullPath(root);
        CodeCompassConfig.Load(root);
        var ignore = new IgnoreRules();

        // Collect every file that a grammar covers, grouped by extension (so .h and .cpp - same
        // grammar, very different cost - report separately, which is exactly the interesting split).
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

        foreach (var (ext, list) in byExt.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            list.Sort((a, b) => a.Size.CompareTo(b.Size)); // ascending: never surprise-hang on the biggest first
            var lang = LanguageRegistry.ForExtension(ext)!.Key;

            // Warm the grammar/query load so its one-time cost isn't charged to the first real file.
            try { extractor.ExtractUncapped("warm" + ext, "int x;"); } catch { /* best effort */ }

            var parses = new List<FileParse>();
            FileParse? knee = null;
            int largerNotProfiled = 0;
            long largerMax = 0;

            foreach (var (rel, full, size) in list)
            {
                if (knee is not null) { largerNotProfiled++; largerMax = Math.Max(largerMax, size); continue; }
                if (size > hardCeilingBytes) { largerNotProfiled++; largerMax = Math.Max(largerMax, size); continue; }

                string text;
                try { text = TextDecoder.FromBytes(File.ReadAllBytes(full)); }
                catch { continue; }

                var (ms, count, timedOut) = TimeParse(extractor, rel, text, timeoutMs);
                var fp = new FileParse(rel, size, count, ms, timedOut);
                parses.Add(fp);
                all.Add(fp);

                // A timeout, or a completed-but-alarmingly-slow parse, marks the knee: bigger files in
                // this language can only be worse, so stop here rather than risk a real hang.
                if (timedOut || ms >= timeoutMs / 2.0) knee = fp;
            }

            var withSym = parses.Where(p => p.Symbols > 0).Select(p => p.Bytes).OrderBy(b => b).ToList();
            var slowest = parses.Count > 0 ? parses.OrderByDescending(p => p.ParseMs).First() : null;
            string maxPath = withSym.Count > 0
                ? parses.Where(p => p.Symbols > 0).OrderByDescending(p => p.Bytes).First().Path
                : "";

            profiles.Add(new LanguageProfile(
                ext, lang,
                parses.Count,
                withSym.Count,
                Percentile(withSym, 0.50),
                Percentile(withSym, 0.95),
                withSym.Count > 0 ? withSym[^1] : 0,
                maxPath,
                slowest?.ParseMs ?? 0,
                slowest?.Bytes ?? 0,
                knee,
                largerNotProfiled,
                largerMax));
        }

        profiles.Sort((a, b) => b.SymbolBearingMax.CompareTo(a.SymbolBearingMax));
        return new SymbolProfileReport(timeoutMs, hardCeilingBytes, profiles, all);
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
