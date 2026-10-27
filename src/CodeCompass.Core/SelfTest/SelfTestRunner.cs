using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CodeCompass.Core.Config;
using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols;

namespace CodeCompass.Core.SelfTest;

/// <param name="Root">The repo to test. Only read: the self-test writes nothing into it (its index cache is written, as
/// any use writes it).</param>
/// <param name="CliExe">The CLI, run as a child process for the timed, memory-sampled full index.</param>
/// <param name="McpExe">The MCP server, driven over stdio the way Claude Code and Codex drive it.</param>
public sealed record SelfTestOptions(string Root, string CliExe, string McpExe, bool Anonymize = false, Action<string>? Progress = null)
{
    /// <summary>Kill the index or the server past this working set (default 60% of the machine's memory); 0 = never.</summary>
    public long CeilingBytes { get; init; } = ProcessSampler.DefaultCeilingBytes();
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan IndexTimeout { get; init; } = TimeSpan.FromHours(6);
    /// <summary>How many of the repo's broadest names the deep find_references sweep looks up.</summary>
    public int SweepCount { get; init; } = 5;
}

/// <summary>
/// The standard user test: a full index, then every MCP tool as an agent calls it, each answer checked against the files
/// on disk, then a find_references sweep over the repo's broadest names (the costliest lookups there are) for time and
/// memory. Picks its own inputs from the repo, so it runs on any repo - C#, C/C++, mixed, or anything else.
/// </summary>
public static class SelfTestRunner
{
    private const int MaxChecked = 50; // answer rows checked against the files, per call

    public static SelfTestResult Run(SelfTestOptions o)
    {
        var root = Path.GetFullPath(o.Root);
        var name = new ReportNamer(o.Anonymize, root, LinkStore.Read(root));
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var r = new SelfTestResult
        {
            Version = BuildInfo.Version, Anonymized = o.Anonymize, StartedUtc = DateTime.UtcNow,
            RepoLabel = o.Anonymize ? name.RepoLabel : (folder.Length > 0 ? folder : root),
        };
        var total = Stopwatch.StartNew();
        try { RunSteps(o, root, folder, name, r); }
        catch (Exception ex)
        {
            r.Steps.Add(new SelfTestStep("Self-test", "runs to the end", StepOutcome.Fail,
                "the self-test itself failed: " + name.Raw($"{ex.GetType().Name}: {ex.Message}")));
        }
        r.Seconds = total.Elapsed.TotalSeconds;
        return r;
    }

    private static void RunSteps(SelfTestOptions o, string root, string folder, ReportNamer name, SelfTestResult r)
    {
        void Say(string s) => o.Progress?.Invoke(s);

        // ---- the machine and the repo ----
        double ramGb = 0;
        try { ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0; } catch { }
        r.Facts.Add(("CodeCompass", BuildInfo.Version));
        r.Facts.Add(("Machine", $"{Environment.ProcessorCount} core(s) available, {ramGb:F0} GB RAM, {RuntimeInformation.OSDescription}"));
        if (!o.Anonymize) r.Facts.Add(("Repo path", root));
        bool network = NetworkPath.IsNetwork(root);
        r.Facts.Add(("Location", network ? "network share" : "local disk"));
        var cfg = CodeCompassConfig.ReadFrom(root);
        r.Facts.Add(("Settings", cfg is null ? "defaults (no .codecompass.json)" : ".codecompass.json"));
        bool overAuto = RepositoryIndexer.ExceedsAutoLimit(root, cfg ?? new RepoConfig(), out _);
        bool autoReconciles = RepositoryIndexer.AutoReconciles(root);
        r.Facts.Add(("Rescans by itself", autoReconciles
            ? "yes - after a lost file event or an ignore-file change, it walks the repo itself"
            : "no - " + (CodeCompassConfig.AutoReconcile(cfg ?? new RepoConfig()) == false ? "autoReconcile is off in its settings"
                        : network ? "a network share" : "over the auto size limit") + ": `codecompass update` brings it up to date"));

        // ---- index: a full build in a child process, timed and memory-sampled ----
        Say("indexing (full build)...");
        var ix = RunSampled(o.CliExe, ["index", root], o.CeilingBytes, o.IndexTimeout);
        var built = Regex.Match(ix.Stdout, @"Indexed ([\d,]+) files \(([\d.]+) MB\) in ([\d.]+)s");
        var symbolsLine = Regex.Match(ix.Stdout, @"Symbols: ([\d,]+)");
        bool indexed = ix.Exit == 0 && !ix.Killed && !ix.TimedOut;
        r.Steps.Add(new SelfTestStep("Indexing", "full index (codecompass index)",
            indexed ? StepOutcome.Pass : StepOutcome.Fail,
            indexed
                ? (built.Success ? $"{built.Groups[1].Value} files, {built.Groups[2].Value} MB" : "built") +
                  (symbolsLine.Success ? $", {symbolsLine.Groups[1].Value} symbols" : "")
                : ix.Killed ? $"killed at the {o.CeilingBytes / 1048576:N0} MB memory ceiling"
                : ix.TimedOut ? $"still running after {o.IndexTimeout.TotalHours:F0} h"
                : $"exit code {ix.Exit}: {name.Raw(Tail(ix.Stderr, 400))}",
            ix.Ms, ix.PeakMb, o.Anonymize ? null : ix.Stdout.Trim()));
        if (!indexed) return;

        var meta = IndexMetaFile.Read(root);
        if (meta is not null)
        {
            r.Facts.Add(("Files indexed", meta.Files.ToString("N0")));
            if (built.Success) r.Facts.Add(("Text indexed", built.Groups[2].Value + " MB"));
            if (symbolsLine.Success) r.Facts.Add(("Symbols", symbolsLine.Groups[1].Value));
            r.Facts.Add(("Left out by ignore files", $"{meta.IgnoredDirs:N0} folder(s), {meta.IgnoredFiles:N0} file(s)"));
            if (meta.FilesOverCap > 0) r.Facts.Add(("Over the file size cap", $"{meta.FilesOverCap:N0} file(s), not indexed"));
            if (meta.FilesSymbolSkipped > 0) r.Facts.Add(("Text only (no symbols)", $"{meta.FilesSymbolSkipped:N0} file(s)"));
            if (!string.IsNullOrEmpty(meta.Landscape)) r.Facts.Add(("Repo shape", meta.Landscape!));
            if (meta.DroppedDirs > 0)
                r.Steps.Add(new SelfTestStep("Indexing", "every folder read", StepOutcome.Warn,
                    $"{meta.DroppedDirs:N0} folder(s) couldn't be read and aren't indexed (usually a passing network error: run update)"));
        }
        try
        {
            var cacheDir = IndexStore.CacheDirPath(root);
            long bytes = new DirectoryInfo(cacheDir).EnumerateFiles().Sum(f => f.Length);
            r.Facts.Add(("Index on disk", $"{bytes / 1048576.0:N1} MB"));
        }
        catch { }
        foreach (var c in RepoDiagnostics.HealthChecks(root))
            r.Steps.Add(new SelfTestStep("Health", name.Text(c.Name), c.Ok ? StepOutcome.Pass : StepOutcome.Warn, name.Text(c.Detail)));

        // ---- inputs, picked from the index ----
        Say("picking test inputs from the index...");
        var picks = Picks.From(root, o.SweepCount);
        if (picks is null)
        {
            r.Steps.Add(new SelfTestStep("Indexing", "index loads", StepOutcome.Fail, "the index just built doesn't load"));
            return;
        }
        r.Facts.Add(("Files by extension", picks.Extensions));
        if (picks.Defs.Count == 0)
            r.Steps.Add(new SelfTestStep("Indexing", "symbols found", StepOutcome.Warn,
                "no symbols to test with - the repo may hold no code in a language with symbol support"));

        // ---- the tools, over stdio ----
        Say("starting the MCP server...");
        McpStdioSession session;
        try { session = McpStdioSession.Start(o.McpExe, root, o.CeilingBytes, TimeSpan.FromMinutes(10)); }
        catch (Exception ex)
        {
            r.Steps.Add(new SelfTestStep("MCP server", "starts and answers initialize", StepOutcome.Fail, name.Raw(ex.Message)));
            return;
        }
        using (session)
        {
            r.ServerStartMb = session.Memory.LastMb;
            r.Steps.Add(new SelfTestStep("MCP server", "starts and answers initialize", StepOutcome.Pass, "", -1, r.ServerStartMb));
            var lines = new RepoLines(root);
            var t = new Tester(session, lines, name, o, r);

            Say("search_code...");
            foreach (var s in picks.Searches) t.SearchCode(s);
            if (picks.Searches.Count > 0) t.SearchCodeAnyCase(picks.Searches[0]);
            t.SearchCodeNoMatch();

            Say("find_definition...");
            foreach (var s in picks.Defs) t.FindDefinition(s);

            Say("search_symbols...");
            foreach (var s in picks.Defs.Take(3)) t.SearchSymbols(s);

            Say("find_references...");
            foreach (var s in picks.Searches) t.FindReferences(s);

            Say("find_callees...");
            if (!picks.HasCSharp)
                r.Steps.Add(new SelfTestStep("find_callees", "C# methods", StepOutcome.NotApplicable, "no C# in this repo - find_callees is C# only"));
            else if (picks.Callers.Count == 0)
                r.Steps.Add(new SelfTestStep("find_callees", "C# methods", StepOutcome.NotApplicable, "no C# method with a body found to test with"));
            else foreach (var s in picks.Callers) t.FindCallees(s);

            Say("manage_links...");
            t.ManageLinks(folder, picks.Searches.FirstOrDefault());

            Say($"deep find_references sweep ({picks.Broad.Count} broadest names)...");
            foreach (var n in picks.Broad) t.SweepReferences(n);

            Say("reindex...");
            t.Reindex(overAuto, meta?.Files);

            r.ServerPeakMb = session.Memory.PeakMb;
            r.ServerEndMb = session.Memory.LastMb;
            if (session.Memory.Killed)
                r.Steps.Add(new SelfTestStep("MCP server", "stays under the memory ceiling", StepOutcome.Fail,
                    $"killed at {o.CeilingBytes / 1048576:N0} MB"));
            else if (session.Exited)
                r.Steps.Add(new SelfTestStep("MCP server", "stays up", StepOutcome.Fail, "the server exited: " + name.Raw(Tail(session.StderrTail, 400))));
        }
    }

    private static string Tail(string s, int max) => s.Length <= max ? s.Trim() : "..." + s[^max..].Trim();

    // ---- a child process, run to the end with its memory sampled ----

    private sealed record Sampled(int Exit, long Ms, double PeakMb, bool Killed, bool TimedOut, string Stdout, string Stderr);

    private static Sampled RunSampled(string exe, string[] args, long ceilingBytes, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var sampler = new ProcessSampler(p, ceilingBytes);
        bool timedOut = !p.WaitForExit(timeout);
        if (timedOut) { try { p.Kill(entireProcessTree: true); } catch { } }
        p.WaitForExit();
        sw.Stop();
        return new Sampled(p.ExitCode, sw.ElapsedMilliseconds, sampler.PeakMb, sampler.Killed, timedOut,
                           stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    // ---- what to look up, picked from the repo's own index ----

    internal sealed class Picks
    {
        private const int PoolSample = 4000;        // symbols sampled from the index for the pool
        private const int BreadthCandidates = 200;  // names whose uses are counted to find the sweep's broadest
        public List<Symbol> Defs { get; } = new();      // defined once: find_definition, search_symbols
        public List<Symbol> Searches { get; } = new();  // a few uses each: search_code (all of them shown), find_references
        public List<Symbol> Callers { get; } = new();   // C# methods with a body: find_callees
        public List<Symbol> Broad { get; } = new();     // the most used names: the deep sweep
        public bool HasCSharp { get; private set; }
        public string Extensions { get; private set; } = "";

        public static Picks? From(string root, int sweepCount)
        {
            if (!RepositoryIndexer.TryLoad(root, out var text, out var symbols)) return null;
            using (text) using (symbols)
            {
                var p = new Picks();
                var paths = text.AllPaths();
                p.HasCSharp = paths.Any(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                p.Extensions = string.Join(", ", paths
                    .GroupBy(x => Path.GetExtension(x).ToLowerInvariant() is { Length: > 0 } e ? e : "(none)")
                    .OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} {g.Count():N0}"));

                // A pool sampled evenly over the whole index (a query's matches come back in storage order, so they'd all
                // be from one end of the repo), and then spread across file types so a mixed repo tests each language.
                var pool = new List<Symbol>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in symbols.Sample(PoolSample))
                    if (s.Name.Length is >= 5 and <= 40 && IsIdentifier(s.Name) && seen.Add(s.Name)) pool.Add(s);
                if (p.HasCSharp && !pool.Any(IsCSharpMethod))
                    foreach (var s in symbols.Sample(PoolSample * 10))
                        if (IsCSharpMethod(s) && s.Name.Length is >= 5 and <= 40 && IsIdentifier(s.Name) && seen.Add(s.Name)) pool.Add(s);
                var spread = Spread(pool);

                foreach (var s in spread)
                {
                    if (p.Defs.Count >= 5) break;
                    if (symbols.FindByName(s.Name).Count == 1) p.Defs.Add(s);
                }
                // Used in a few places: every hit of a search fits in one answer, so the definition's own line must be in it.
                var breadth = new List<(Symbol Sym, int Hits)>();
                foreach (var s in spread.Take(BreadthCandidates))
                {
                    int hits = text.Search(s.Name, 1001, caseSensitive: true).Count;
                    breadth.Add((s, hits));
                    if (p.Searches.Count < 4 && hits is >= 2 and <= 40 && symbols.FindByName(s.Name).Count == 1) p.Searches.Add(s);
                }
                p.Broad.AddRange(breadth.Where(b => b.Hits > 0).OrderByDescending(b => b.Hits).Take(sweepCount).Select(b => b.Sym));
                foreach (var s in spread)
                {
                    if (p.Callers.Count >= 4) break;
                    if (IsCSharpMethod(s) && s.EndLine - s.Line >= 2 && symbols.FindByName(s.Name).Count == 1) p.Callers.Add(s);
                }
                return p;
            }
        }

        private static bool IsCSharpMethod(Symbol s) => s.Kind == SymbolKind.Method && s.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        private static bool IsIdentifier(string s) => (char.IsLetter(s[0]) || s[0] == '_') && s.All(AnswerChecks.IsIdentChar);

        // Round-robin over file extensions: one from each in turn.
        private static List<Symbol> Spread(List<Symbol> pool)
        {
            var queues = pool.GroupBy(s => Path.GetExtension(s.RelativePath).ToLowerInvariant())
                             .OrderByDescending(g => g.Count()).Select(g => new Queue<Symbol>(g)).ToList();
            var outList = new List<Symbol>(pool.Count);
            while (queues.Any(q => q.Count > 0))
                foreach (var q in queues)
                    if (q.Count > 0) outList.Add(q.Dequeue());
            return outList;
        }
    }

    // ---- the checks ----

    private sealed class Tester(McpStdioSession session, RepoLines lines, ReportNamer name, SelfTestOptions o, SelfTestResult r)
    {
        private McpCallResult Call(string tool, Dictionary<string, object?> args) => session.Call(tool, args, o.CallTimeout);

        private void Add(string feature, string what, StepOutcome outcome, string detail, McpCallResult res) =>
            r.Steps.Add(new SelfTestStep(feature, what, outcome, detail, res.Ms, res.PeakMb, o.Anonymize ? null : res.Text));

        private static bool SamePath(string answerPath, string relPath) =>
            string.Equals(answerPath.Replace('\\', '/'), relPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        // Rows checked against the file: right, wrong column (the text is on the line, elsewhere), wrong, or not checked.
        private sealed class Tally
        {
            public int Ok, Column, Wrong, Unchecked;
            public readonly List<string> Bad = new();
            public bool Clean => Column == 0 && Wrong == 0;
            public string Summary =>
                $"{Ok + Column + Wrong} checked against the file" +
                (Column > 0 ? $", {Column} at the wrong column" : "") +
                (Wrong > 0 ? $", {Wrong} WRONG" : "") +
                (Unchecked > 0 ? $", {Unchecked} not checked (file too big or gone)" : "") +
                (Bad.Count > 0 ? " - e.g. " + string.Join("; ", Bad.Take(3)) : "");
        }

        private Tally Check(IEnumerable<AnswerHit> hits, Func<string, AnswerHit, bool> atColumn, Func<string, AnswerHit, bool> onLine)
        {
            var t = new Tally();
            foreach (var h in hits.Take(MaxChecked))
            {
                var line = lines.Line(h.Path, h.Line);
                if (line is null)
                {
                    if (lines.TooBig(h.Path)) t.Unchecked++;
                    else { t.Wrong++; t.Bad.Add($"{name.File(h.Path)}:{h.Line} has no such line"); }
                }
                else if (atColumn(line, h)) t.Ok++;
                else if (onLine(line, h)) { t.Column++; t.Bad.Add($"{name.File(h.Path)}:{h.Line}:{h.Column} column"); }
                else { t.Wrong++; t.Bad.Add($"{name.File(h.Path)}:{h.Line} doesn't hold it"); }
            }
            return t;
        }

        public void SearchCode(Symbol s)
        {
            const string F = "search_code";
            var what = $"\"{name.Sym(s.Name)}\"";
            var res = Call(F, new() { ["query"] = s.Name, ["maxResults"] = 50 });
            if (res.IsError) { Add(F, what, StepOutcome.Fail, name.Raw(res.Text), res); return; }
            var hits = AnswerChecks.Hits(res.Text);
            var tally = Check(hits, (line, h) => h.Column >= 1 && line.Length >= h.Column - 1 + s.Name.Length &&
                                                 string.CompareOrdinal(line, h.Column - 1, s.Name, 0, s.Name.Length) == 0,
                              (line, _) => line.Contains(s.Name, StringComparison.Ordinal));
            bool truncated = AnswerChecks.Truncated(res.Text);
            bool foundDef = hits.Any(h => SamePath(h.Path, s.RelativePath) && h.Line == s.Line);
            bool ok = hits.Count > 0 && tally.Clean && (truncated || foundDef);
            Add(F, what, ok ? StepOutcome.Pass : StepOutcome.Fail,
                $"{hits.Count} hit(s){(truncated ? " (more exist)" : "")}; {tally.Summary}" +
                (truncated ? "" : foundDef ? "; its definition's line is among them" : "; its definition's line is MISSING"), res);
        }

        public void SearchCodeAnyCase(Symbol s)
        {
            const string F = "search_code";
            var q = s.Name.ToLowerInvariant() != s.Name ? s.Name.ToLowerInvariant() : s.Name.ToUpperInvariant();
            var what = $"\"{name.Sym(s.Name)}\" in another case, caseSensitive=false";
            var res = Call(F, new() { ["query"] = q, ["maxResults"] = 50, ["caseSensitive"] = false });
            if (res.IsError) { Add(F, what, StepOutcome.Fail, name.Raw(res.Text), res); return; }
            var hits = AnswerChecks.Hits(res.Text);
            var tally = Check(hits, (line, h) => h.Column >= 1 && line.Length >= h.Column - 1 + q.Length &&
                                                 string.Compare(line, h.Column - 1, q, 0, q.Length, StringComparison.OrdinalIgnoreCase) == 0,
                              (line, _) => line.Contains(q, StringComparison.OrdinalIgnoreCase));
            bool truncated = AnswerChecks.Truncated(res.Text);
            bool foundDef = hits.Any(h => SamePath(h.Path, s.RelativePath) && h.Line == s.Line);
            bool ok = hits.Count > 0 && tally.Clean && (truncated || foundDef);
            Add(F, what, ok ? StepOutcome.Pass : StepOutcome.Fail,
                $"{hits.Count} hit(s){(truncated ? " (more exist)" : "")}; {tally.Summary}" +
                (truncated ? "" : foundDef ? "; its definition's line is among them" : "; its definition's line is MISSING"), res);
        }

        public void SearchCodeNoMatch()
        {
            const string F = "search_code";
            var q = "zqx" + Guid.NewGuid().ToString("N")[..12];
            var res = Call(F, new() { ["query"] = q, ["maxResults"] = 50 });
            bool ok = !res.IsError && AnswerChecks.Hits(res.Text).Count == 0 && res.Text.StartsWith("No matches", StringComparison.Ordinal);
            Add(F, "text that isn't in the repo", ok ? StepOutcome.Pass : StepOutcome.Fail,
                ok ? "says there's no match" : "expected a no-match answer", res);
        }

        public void FindDefinition(Symbol s)
        {
            const string F = "find_definition";
            var what = $"{name.Sym(s.Name)} ({s.Kind}, {Path.GetExtension(s.RelativePath)})";
            var res = Call(F, new() { ["name"] = s.Name });
            if (res.IsError) { Add(F, what, StepOutcome.Fail, name.Raw(res.Text), res); return; }
            var defs = AnswerChecks.Defs(res.Text);
            var tally = Check(defs.Select(d => new AnswerHit(d.Path, d.Line, d.Column, d.Name)),
                              (line, h) => AnswerChecks.WholeWordAt(line, h.Column, s.Name),
                              (line, _) => AnswerChecks.HasWholeWord(line, s.Name));
            bool found = defs.Any(d => SamePath(d.Path, s.RelativePath) && d.Line == s.Line);
            bool inlined = defs.Count == 1 && res.Text.Contains("\n\n", StringComparison.Ordinal);
            bool ok = found && tally.Clean;
            Add(F, what, ok ? StepOutcome.Pass : StepOutcome.Fail,
                $"{defs.Count} definition(s); {tally.Summary}" + (found ? "" : "; the expected definition is MISSING") +
                (inlined ? "; source inlined" : ""), res);
        }

        public void SearchSymbols(Symbol s)
        {
            const string F = "search_symbols";
            var q = s.Name.Length > 6 ? s.Name[1..^1] : s.Name; // a substring: not the name's start, not its end
            var what = $"\"{name.Sym(q)}\" (part of {name.Sym(s.Name)})";
            var res = Call(F, new() { ["query"] = q, ["maxResults"] = 200 });
            if (res.IsError) { Add(F, what, StepOutcome.Fail, name.Raw(res.Text), res); return; }
            var rows = AnswerChecks.Hits(res.Text);
            var names = rows.Select(h => h.Text[(h.Text.IndexOf(' ') + 1)..]).ToList();
            int notMatching = names.Count(n => !n.Contains(q, StringComparison.OrdinalIgnoreCase));
            var tally = Check(rows.Select((h, i) => h with { Text = names[i] }),
                              (line, h) => AnswerChecks.WholeWordAt(line, h.Column, h.Text),
                              (line, h) => AnswerChecks.HasWholeWord(line, h.Text));
            bool truncated = AnswerChecks.Truncated(res.Text);
            bool found = rows.Any(h => SamePath(h.Path, s.RelativePath) && h.Line == s.Line);
            bool ok = rows.Count > 0 && notMatching == 0 && tally.Clean && (truncated || found);
            Add(F, what, ok ? StepOutcome.Pass : StepOutcome.Fail,
                $"{rows.Count} symbol(s){(truncated ? " (more exist)" : "")}" +
                (notMatching > 0 ? $", {notMatching} DON'T contain the query" : ", all contain the query") + $"; {tally.Summary}" +
                (truncated ? "" : found ? "; the expected symbol is among them" : "; the expected symbol is MISSING"), res);
        }

        private static bool IsType(Symbol s) =>
            s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Record or SymbolKind.Interface or SymbolKind.Enum;

        // A find_references answer, checked: rows that hold the name (or are where C# calls it unseen), the footer, and
        // the same answer when asked again. "No references found" for a name the text holds is a warning; any other
        // answer without rows - a failure message, an answer cut short - is a failure.
        private (StepOutcome Outcome, string Detail, List<AnswerHit> Hits) CheckRefs(Symbol s, McpCallResult a, McpCallResult b)
        {
            var n = s.Name;
            var hits = AnswerChecks.Hits(a.Text);
            var shape = a.IsError ? RefsAnswer.Other : AnswerChecks.RefsShape(a.Text);
            bool same = !b.IsError && a.Text == b.Text;
            string again = $"asked again: {(same ? "the same answer" : "a DIFFERENT answer")} ({b.Ms:N0} ms)";
            if (shape == RefsAnswer.Other) return (StepOutcome.Fail, "not a references answer: " + name.Raw(a.Text), hits);
            if (shape == RefsAnswer.NoneFound)
                return (same ? StepOutcome.Warn : StepOutcome.Fail,
                        $"no references found, though the name is in the text (its other uses may be in comments or strings); {again}", hits);
            var tally = Check(hits, (line, h) => AnswerChecks.ReferenceAt(h.Path, line, h.Column, n, IsType(s)),
                              (line, _) => AnswerChecks.ReferenceOnLine(line, n));
            var split = AnswerChecks.RefsBreakdown(a.Text)!.Value;
            bool implicitOnly = !tally.Clean && AnswerChecks.CanBeImplicit(s.RelativePath, n);
            var outcome = !same ? StepOutcome.Fail : tally.Clean ? StepOutcome.Pass : implicitOnly ? StepOutcome.Warn : StepOutcome.Fail;
            return (outcome,
                    $"{hits.Count} shown{(AnswerChecks.Truncated(a.Text) ? " (more exist)" : "")}: {split.Semantic} C# semantic + {split.Named} by name; " +
                    tally.Summary + (implicitOnly ? " (C# can call it where the code doesn't spell it: a using, foreach, await...)" : "") +
                    "; " + again, hits);
        }

        public void FindReferences(Symbol s)
        {
            const string F = "find_references";
            var a = Call(F, new() { ["name"] = s.Name, ["maxResults"] = 100 });
            var b = a.IsError ? a : Call(F, new() { ["name"] = s.Name, ["maxResults"] = 100 });
            var (outcome, detail, _) = CheckRefs(s, a, b);
            Add(F, name.Sym(s.Name), outcome, detail, a);
        }

        public void SweepReferences(Symbol s)
        {
            const string F = "find_references (deep)";
            var a = Call("find_references", new() { ["name"] = s.Name, ["maxResults"] = 1000 });
            var b = a.IsError ? a : Call("find_references", new() { ["name"] = s.Name, ["maxResults"] = 1000 });
            var (outcome, detail, hits) = CheckRefs(s, a, b);
            var split = AnswerChecks.RefsBreakdown(a.Text) ?? (0, 0);
            r.Sweep.Add(new RefsSweepRow(name.Sym(s.Name), hits.Count, AnswerChecks.Truncated(a.Text), split.Semantic, split.Named,
                                         a.Ms, a.PeakMb, b.Ms, !b.IsError && a.Text == b.Text, outcome != StepOutcome.Fail, detail));
            Add(F, name.Sym(s.Name), outcome, detail, a);
        }

        public void FindCallees(Symbol s)
        {
            const string F = "find_callees";
            var what = name.Sym(s.Name);
            var res = Call(F, new() { ["name"] = s.Name, ["maxResults"] = 50 });
            if (res.IsError) { Add(F, what, StepOutcome.Fail, name.Raw(res.Text), res); return; }
            var callees = AnswerChecks.Hits(res.Text);
            if (callees.Count == 0)
            {
                bool said = res.Text.Contains("calls no in-repo methods", StringComparison.Ordinal);
                Add(F, what, said ? StepOutcome.Pass : StepOutcome.Fail,
                    said ? "calls no in-repo methods (said so)" : "no callees, and the answer doesn't say why", res);
                return;
            }
            // Each callee row is the callee's definition: its name is there, and the caller's body names it.
            var body = lines.Span(s.RelativePath, s.Line, Math.Max(s.Line, s.EndLine)) ?? "";
            int ok = 0, notNamed = 0, noDef = 0, unchecked_ = 0;
            var odd = new List<string>();
            foreach (var h in callees.Take(MaxChecked))
            {
                var line = lines.Line(h.Path, h.Line);
                if (line is null) { if (lines.TooBig(h.Path)) unchecked_++; else { noDef++; odd.Add($"{name.File(h.Path)}:{h.Line} has no such line"); } continue; }
                var callee = AnswerChecks.IdentifierAt(line, h.Column);
                if (callee.Length == 0 || callee == "operator") { unchecked_++; continue; }
                if (AnswerChecks.HasWholeWord(body, callee)) ok++;
                else { notNamed++; odd.Add(name.Sym(callee)); }
            }
            // A callee the body doesn't spell out may be called implicitly (a using's Dispose, a foreach's GetEnumerator):
            // worth a look, not a failure. A row that isn't a definition at all is wrong.
            var outcome = noDef > 0 ? StepOutcome.Fail : notNamed > 0 ? StepOutcome.Warn : StepOutcome.Pass;
            Add(F, what, outcome,
                $"{callees.Count} callee(s); {ok} named in the caller's body" +
                (notNamed > 0 ? $", {notNamed} not spelled out there (implicit calls?)" : "") +
                (noDef > 0 ? $", {noDef} WRONG" : "") + (unchecked_ > 0 ? $", {unchecked_} not checked" : "") +
                (odd.Count > 0 ? " - " + string.Join(", ", odd.Take(4)) : ""), res);
        }

        public void ManageLinks(string folder, Symbol? sample)
        {
            const string F = "manage_links";
            var links = LinkStore.Read(o.Root);
            var list = Call(F, new() { ["action"] = "list" });
            bool listed = !list.IsError && links.All(l => list.Text.Contains(Path.GetFileName(Path.TrimEndingDirectorySeparator(l)), StringComparison.OrdinalIgnoreCase));
            Add(F, "list", listed ? StepOutcome.Pass : StepOutcome.Fail,
                links.Count == 0 ? "no linked roots" : $"{links.Count} linked root(s), all listed", list);

            // Focus on the project itself: the searches still find what's in it, and clearing the focus answers.
            var focus = Call(F, new() { ["action"] = "focus", ["path"] = folder });
            bool took = !focus.IsError && AnswerChecks.FocusTook(focus.Text);
            Add(F, $"focus on the project ({name.Folder(folder)})", took ? StepOutcome.Pass : StepOutcome.Fail,
                took ? "focused" : "the focus didn't take: " + name.Raw(focus.Text), focus);
            if (sample is { } s)
            {
                var res = Call("search_code", new() { ["query"] = s.Name, ["maxResults"] = 50 });
                var hits = AnswerChecks.Hits(res.Text);
                bool found = AnswerChecks.Truncated(res.Text) ? hits.Count > 0 : hits.Any(h => SamePath(h.Path, s.RelativePath) && h.Line == s.Line);
                Add(F, $"search_code \"{name.Sym(s.Name)}\" while focused", found ? StepOutcome.Pass : StepOutcome.Fail,
                    found ? "still finds it in the project" : "the focus hid the project's own hit", res);
            }
            var clear = Call(F, new() { ["action"] = "focus" });
            Add(F, "clear the focus", clear.IsError ? StepOutcome.Fail : StepOutcome.Pass, clear.IsError ? name.Raw(clear.Text) : "cleared", clear);
        }

        public void Reindex(bool overAuto, int? metaFiles)
        {
            const string F = "reindex";
            var res = Call(F, new());
            if (overAuto)
            {
                bool refused = !res.IsError && res.Text.Contains("codecompass index", StringComparison.Ordinal);
                Add(F, "on a repo over the auto size limit", refused ? StepOutcome.Pass : StepOutcome.Fail,
                    refused ? "points at `codecompass index` instead of timing out the call" : "expected it to point at `codecompass index`", res);
                return;
            }
            var m = Regex.Match(res.Text, @"^Reindexed (\d+) files");
            if (res.IsError || !m.Success)
            {
                bool busy = res.Text.Contains("writing this index right now", StringComparison.Ordinal);
                Add(F, "rebuild", busy ? StepOutcome.Warn : StepOutcome.Fail, busy ? "another process was writing the index" : name.Raw(res.Text), res);
                return;
            }
            int files = int.Parse(m.Groups[1].Value);
            bool same = metaFiles is null || metaFiles == files;
            Add(F, "rebuild", same ? StepOutcome.Pass : StepOutcome.Warn,
                $"{files:N0} files" + (same ? ", as the full index found" : $" - the full index found {metaFiles:N0} (did the repo change meanwhile?)"), res);
        }
    }
}
