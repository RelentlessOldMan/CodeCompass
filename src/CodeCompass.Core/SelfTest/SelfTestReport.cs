using System.Net;
using System.Text;

namespace CodeCompass.Core.SelfTest;

/// <summary>The self-test's outputs: one self-contained HTML page (no scripts, no external files) and a short text summary.</summary>
public static class SelfTestReport
{
    /// <summary>The features in report order, with what each is for (the report's summary table).</summary>
    private static readonly (string Feature, string About)[] Features =
    [
        ("Indexing", "full index of the repo, timed, peak memory sampled"),
        ("Health", "the doctor's checks on the index just built"),
        ("MCP server", "the server as Claude Code and Codex run it, over stdio"),
        ("search_code", "text search: every hit is on its line, at its column"),
        ("find_definition", "definitions: at the right line and column, the expected one found"),
        ("search_symbols", "symbol search: every hit contains the query and names a real declaration"),
        ("find_references", "references: each names the symbol; asked twice, the same answer"),
        ("find_callees", "C# callees: each is a definition the caller's body names"),
        ("manage_links", "linked roots listed; a session focus keeps the project's hits"),
        ("find_references (deep)", "the repo's broadest names, up to 1,000 references each: time and memory"),
        ("reindex", "a rebuild from inside a session (or the refusal for a big repo)"),
    ];

    public static string Summary(SelfTestResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CodeCompass self-test of {r.RepoLabel}: {Verdict(r)} ({r.Passed} passed, {r.Warned} warning(s), {r.Failed} failed) in {Duration(r.Seconds)}");
        foreach (var (feature, _) in Features)
        {
            var steps = r.Steps.Where(s => s.Feature == feature).ToList();
            if (steps.Count == 0) continue;
            sb.AppendLine($"  {feature,-24} {FeatureOutcome(steps)}");
            foreach (var s in steps.Where(s => s.Outcome is StepOutcome.Fail or StepOutcome.Warn))
                sb.AppendLine($"      {(s.Outcome == StepOutcome.Fail ? "FAIL" : "warn")} {s.What}: {s.Detail}");
        }
        return sb.ToString();
    }

    public static string Html(SelfTestResult r)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Self-test {Enc(r.RepoLabel)}</title><style>");
        sb.Append("""
            :root{--bg:#f6f8fa;--card:#fff;--ink:#1f2328;--muted:#59636e;--line:#d1d9e0;--head:#eef1f4;--good:#1a7f37;--bad:#cf222e;--warn:#9a6700;--accent:#0969da;--code:#f6f8fa}
            @media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#0f1720;--card:#1b2430;--ink:#e6edf3;--muted:#9fb0c0;--line:#2b3947;--head:#141d28;--good:#3fb950;--bad:#f0645b;--warn:#d29922;--accent:#4c8bf5;--code:#111a24}}
            :root[data-theme="dark"]{--bg:#0f1720;--card:#1b2430;--ink:#e6edf3;--muted:#9fb0c0;--line:#2b3947;--head:#141d28;--good:#3fb950;--bad:#f0645b;--warn:#d29922;--accent:#4c8bf5;--code:#111a24}
            *{box-sizing:border-box}
            body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;padding:28px 16px}
            .wrap{max-width:1100px;margin:0 auto}
            h1{font-size:22px;margin:0 0 4px} h2{font-size:16px;margin:28px 0 10px} .sub{color:var(--muted);margin:0 0 18px}
            .verdict{display:inline-block;padding:4px 12px;border-radius:999px;font-weight:700;margin-right:8px}
            .meta{display:flex;flex-wrap:wrap;gap:8px;margin:0 0 8px}
            .chip{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:5px 10px;color:var(--muted)}
            .chip b{color:var(--ink);font-weight:600}
            .scroll{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--card)}
            table{width:100%;border-collapse:collapse}
            th,td{padding:8px 10px;text-align:left;border-bottom:1px solid var(--line);vertical-align:top}
            th{background:var(--head);color:var(--muted);font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.03em;white-space:nowrap}
            tr:last-child td{border-bottom:none}
            td.num,th.num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
            td.what{white-space:nowrap} td.detail{min-width:280px}
            .badge{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;font-weight:600;white-space:nowrap}
            .pass{background:color-mix(in srgb,var(--good) 15%,transparent);color:var(--good)}
            .fail{background:color-mix(in srgb,var(--bad) 15%,transparent);color:var(--bad)}
            .warn{background:color-mix(in srgb,var(--warn) 15%,transparent);color:var(--warn)}
            .na,.info{background:color-mix(in srgb,var(--muted) 12%,transparent);color:var(--muted)}
            details summary{cursor:pointer;color:var(--accent);font-size:12px;margin-top:4px}
            pre{background:var(--code);border:1px solid var(--line);border-radius:6px;padding:8px;margin:6px 0 0;max-height:320px;overflow:auto;font:12px/1.4 ui-monospace,Consolas,monospace;white-space:pre}
            .note{color:var(--muted);font-size:13px;margin-top:8px}
            footer{color:var(--muted);margin-top:24px;font-size:12px}
            """);
        sb.Append("</style></head><body><div class=\"wrap\">");

        var verdict = Verdict(r);
        sb.Append($"<h1>CodeCompass self-test: {Enc(r.RepoLabel)}</h1>");
        sb.Append($"<p class=\"sub\"><span class=\"verdict {Cls(verdict)}\">{verdict}</span>");
        sb.Append($"{r.Passed} passed, {r.Warned} warning(s), {r.Failed} failed, in {Duration(r.Seconds)}. ");
        sb.Append("Every tool answer was checked against the repo's files on disk.");
        if (r.Anonymized) sb.Append(" Names and paths are hashed, and the answers are left out.");
        sb.Append("</p>");

        sb.Append("<div class=\"meta\">");
        Chip(sb, "When", r.StartedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC");
        foreach (var (label, value) in r.Facts) Chip(sb, label, value);
        if (r.ServerPeakMb >= 0)
            Chip(sb, "MCP server memory", $"{r.ServerStartMb:N0} MB at start, {r.ServerPeakMb:N0} MB peak, {r.ServerEndMb:N0} MB at the end");
        sb.Append("</div>");

        // ---- one row per feature ----
        sb.Append("<h2>Features</h2><div class=\"scroll\"><table><thead><tr><th>Feature</th><th>Result</th><th class=\"num\">Checks</th><th class=\"num\">Slowest</th><th>What it tests</th></tr></thead><tbody>");
        foreach (var (feature, about) in Features)
        {
            var steps = r.Steps.Where(s => s.Feature == feature).ToList();
            if (steps.Count == 0) continue;
            var outcome = FeatureOutcome(steps);
            long slowest = steps.Max(s => s.Ms);
            sb.Append($"<tr><td><a href=\"#{Anchor(feature)}\">{Enc(feature)}</a></td><td><span class=\"badge {Cls(outcome)}\">{outcome}</span></td>");
            sb.Append($"<td class=\"num\">{steps.Count}</td><td class=\"num\">{(slowest >= 0 ? Ms(slowest) : "")}</td><td>{Enc(about)}</td></tr>");
        }
        sb.Append("</tbody></table></div>");

        // ---- the deep sweep ----
        if (r.Sweep.Count > 0)
        {
            sb.Append("<h2>Deep find_references sweep</h2>");
            sb.Append("<p class=\"note\">The names used most in this repo, each looked up with maxResults=1000 and then again. These are the costliest lookups an agent can make here. Peak is the server's working set during the call.</p>");
            sb.Append("<div class=\"scroll\"><table><thead><tr><th>Name</th><th class=\"num\">Shown</th><th class=\"num\">C# semantic</th><th class=\"num\">By name</th><th class=\"num\">Time</th><th class=\"num\">Again</th><th class=\"num\">Peak MB</th><th>Same answer</th><th>Result</th></tr></thead><tbody>");
            foreach (var w in r.Sweep)
            {
                sb.Append($"<tr><td class=\"what\">{Enc(w.Name)}</td><td class=\"num\">{w.Shown:N0}{(w.Truncated ? "+" : "")}</td>");
                sb.Append($"<td class=\"num\">{w.Semantic:N0}</td><td class=\"num\">{w.Named:N0}</td><td class=\"num\">{Ms(w.Ms)}</td><td class=\"num\">{Ms(w.RepeatMs)}</td>");
                sb.Append($"<td class=\"num\">{w.PeakMb:N0}</td><td>{(w.Deterministic ? "yes" : "<b>NO</b>")}</td>");
                sb.Append($"<td><span class=\"badge {(w.Ok ? "pass" : "fail")}\">{(w.Ok ? "PASS" : "FAIL")}</span></td></tr>");
            }
            sb.Append("</tbody></table></div>");
        }

        // ---- every step, by feature ----
        foreach (var (feature, about) in Features)
        {
            var steps = r.Steps.Where(s => s.Feature == feature).ToList();
            if (steps.Count == 0) continue;
            sb.Append($"<h2 id=\"{Anchor(feature)}\">{Enc(feature)}</h2>");
            sb.Append("<div class=\"scroll\"><table><thead><tr><th>Check</th><th>Result</th><th class=\"num\">Time</th><th class=\"num\">Peak MB</th><th>Detail</th></tr></thead><tbody>");
            foreach (var s in steps)
            {
                sb.Append($"<tr><td class=\"what\">{Enc(s.What)}</td><td><span class=\"badge {Cls(Label(s.Outcome))}\">{Label(s.Outcome)}</span></td>");
                sb.Append($"<td class=\"num\">{(s.Ms >= 0 ? Ms(s.Ms) : "")}</td><td class=\"num\">{(s.PeakMb >= 0 ? s.PeakMb.ToString("N0") : "")}</td>");
                sb.Append($"<td class=\"detail\">{Enc(s.Detail)}");
                if (!string.IsNullOrEmpty(s.Answer))
                    sb.Append($"<details><summary>answer</summary><pre>{Enc(Clip(s.Answer!, 20_000))}</pre></details>");
                sb.Append("</td></tr>");
            }
            sb.Append("</tbody></table></div>");
        }

        sb.Append($"<footer>codecompass selftest, CodeCompass {Enc(r.Version)}. Read-only: nothing in the repo was written. The index cache was rebuilt.</footer>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    public static string Verdict(SelfTestResult r) => r.Failed > 0 ? "FAIL" : r.Warned > 0 ? "PASS with warnings" : "PASS";

    private static string FeatureOutcome(List<SelfTestStep> steps) =>
        steps.Any(s => s.Outcome == StepOutcome.Fail) ? "FAIL"
        : steps.Any(s => s.Outcome == StepOutcome.Warn) ? "WARN"
        : steps.All(s => s.Outcome == StepOutcome.NotApplicable) ? "N/A"
        : "PASS";

    private static string Label(StepOutcome o) => o switch
    {
        StepOutcome.Pass => "PASS", StepOutcome.Fail => "FAIL", StepOutcome.Warn => "WARN", StepOutcome.NotApplicable => "N/A", _ => "INFO",
    };

    private static string Cls(string label) => label switch
    {
        "PASS" => "pass", "FAIL" => "fail", "WARN" or "PASS with warnings" => "warn", "N/A" => "na", _ => "info",
    };

    private static string Anchor(string feature) => "f-" + new string(feature.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());

    private static string Ms(long ms) => ms < 10_000 ? $"{ms:N0} ms" : ms < 600_000 ? $"{ms / 1000.0:N1} s" : $"{ms / 60000.0:N1} min";

    private static string Duration(double s) => s < 120 ? $"{s:N0} s" : s < 7200 ? $"{s / 60:N1} min" : $"{s / 3600:N1} h";

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + $"\n... ({s.Length - max:N0} more characters)";

    private static void Chip(StringBuilder sb, string k, string v) => sb.Append($"<span class=\"chip\">{Enc(k)} <b>{Enc(v)}</b></span>");

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
