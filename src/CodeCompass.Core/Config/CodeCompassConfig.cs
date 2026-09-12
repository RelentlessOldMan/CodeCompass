using System.Text.Json;
using System.Text.Json.Serialization;
using CodeCompass.Core.Diagnostics;

namespace CodeCompass.Core.Config;

/// <summary>Per-repo config read from <c>.codecompass.json</c> at the repo root. Every field is
/// optional; an absent/invalid file just means "use defaults". Committed with the repo, so the
/// settings travel with it.</summary>
public sealed class RepoConfig
{
    [JsonPropertyName("maxSymbolMb")] public long? MaxSymbolMb { get; set; }
    [JsonPropertyName("maxFileMb")] public long? MaxFileMb { get; set; }
    [JsonPropertyName("maxAutoMb")] public long? MaxAutoMb { get; set; }
    [JsonPropertyName("threads")] public int? Threads { get; set; }
    [JsonPropertyName("segmentMb")] public int? SegmentMb { get; set; }
    [JsonPropertyName("compactSegments")] public int? CompactSegments { get; set; }
    [JsonPropertyName("stallWarnSec")] public int? StallWarnSec { get; set; }
    [JsonPropertyName("readBudgetMb")] public long? ReadBudgetMb { get; set; }
    [JsonPropertyName("autoReconcile")] public bool? AutoReconcile { get; set; }
    [JsonPropertyName("statusLine")] public bool? StatusLine { get; set; }
    [JsonPropertyName("ignore")] public string[]? Ignore { get; set; }
}

/// <summary>
/// Resolves every tuning knob from, in order of precedence: an environment variable, the per-repo
/// <c>.codecompass.json</c>, then the built-in default. This is the single place that knows how
/// each knob is configured; the readers (IgnoreRules, the symbol extractor, the indexer, the MCP
/// size gate) delegate here. The active repo config is loaded at each indexing entry point.
/// </summary>
public static class CodeCompassConfig
{
    public const string FileName = ".codecompass.json";

    /// <summary>A documented starter <c>.codecompass.json</c>. Every setting is commented out, so an
    /// unedited copy is equivalent to no file at all (all defaults) - the user uncomments only what
    /// they want to change. Comments/trailing commas are tolerated by the loader. Written by
    /// <c>codecompass init</c>.</summary>
    public const string Template = """
{
  // CodeCompass per-repo config. Optional - without this file everything uses defaults.
  // Precedence: environment variable > this file > default. Sizes are in MB.
  // Uncomment and edit only what you want to change.

  // "maxSymbolMb": 1,        // skip go-to-definition/symbol extraction above this size (default 1).
                              //   Raise for large real code whose symbols you want; numeric data
                              //   blobs above 1 MB are auto-skipped regardless. Still text-searchable.
  // "maxFileMb": 2000,       // don't index a file larger than this at all (default 2000 = 2 GB).
  // "ignore": ["generated", "thirdparty"],  // extra directory names to exclude from indexing.
  // "maxAutoMb": 100,        // repos bigger than this wait for a one-time `codecompass index` instead
                              //   of auto-indexing inside a tool call (default 100).
  // "autoReconcile": true,   // on startup, pick up changes made outside the session (e.g. a source-
                              //   control sync). Default: on for local repos within maxAutoMb; off for
                              //   network shares / huge repos (run `codecompass update` there). true =
                              //   always, false = never.
  // "statusLine": true,      // publish index state for the `codecompass statusline` command (shown in
                              //   Claude Code's status area). Default true; harmless if unused.
  // "threads": 0,            // indexing parallelism; 0 / omitted = all CPU cores.
  // "segmentMb": 0,          // per-worker build-memory budget; 0 / omitted = scaled to RAM.
  // "compactSegments": 64,   // merge on-disk segments after this many accumulate (default 64).
  // "stallWarnSec": 60,      // warn in the log if a build stalls this long (default 60, min 5).
  // "readBudgetMb": 0        // file bytes in flight during a build; 0 = ~1/16 of RAM (256 MB-4 GB).
}
""";

    private static volatile RepoConfig _current = new();

    /// <summary>The config for the repo currently being indexed (empty if none/invalid).</summary>
    public static RepoConfig Current => _current;

    /// <summary>Load <c>&lt;root&gt;/.codecompass.json</c> as the active config. Cheap; safe to call
    /// at every indexing entry point. Env vars still override whatever it contains.</summary>
    public static void Load(string root)
    {
        _current = Read(root) ?? new RepoConfig();
    }

    /// <summary>Parse <c>&lt;dir&gt;/.codecompass.json</c> without touching the ambient config
    /// (null if absent/invalid). Pure - handy for tools and deterministic tests.</summary>
    public static RepoConfig? ReadFrom(string dir) => Read(dir);

    private static RepoConfig? Read(string root)
    {
        try
        {
            var path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return null;
            var cfg = JsonSerializer.Deserialize<RepoConfig>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (cfg is not null) Log.Global.Info($"loaded {FileName} from {root}");
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Global.Warn($"ignoring invalid {FileName} in {root}: {ex.Message}");
            return null;
        }
    }

    // ---- resolvers (env > config file > default) ----

    private static long? EnvLong(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return long.TryParse(v, out var n) ? n : null;
    }

    private static int? EnvInt(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) ? n : null;
    }

    // Accepts true/false and 1/0; null if unset/unrecognized.
    private static bool? EnvBool(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (bool.TryParse(v, out var b)) return b;
        if (v == "1") return true;
        if (v == "0") return false;
        return null;
    }

    /// <summary>Whether the MCP server auto-reconciles the index on startup. Tri-state: null = the
    /// gated default (local + within the auto limit); true = always; false = never. Env
    /// CODECOMPASS_AUTO_RECONCILE / config `autoReconcile`.</summary>
    public static bool? AutoReconcile() => AutoReconcile(_current);
    public static bool? AutoReconcile(RepoConfig cfg) => EnvBool("CODECOMPASS_AUTO_RECONCILE") ?? cfg.AutoReconcile;

    /// <summary>Whether the server publishes its status to a per-repo status file (for the
    /// `codecompass statusline` command). Default true - cheap, and harmless if unused. Env
    /// CODECOMPASS_STATUS_LINE / config `statusLine`.</summary>
    public static bool StatusLinePublish() => StatusLinePublish(_current);
    public static bool StatusLinePublish(RepoConfig cfg) => EnvBool("CODECOMPASS_STATUS_LINE") ?? cfg.StatusLine ?? true;

    // Each knob has a pure overload taking an explicit RepoConfig (deterministic; used by tests and
    // tools) and an ambient no-arg overload that resolves against the active repo config.

    /// <summary>Per-file indexing size cap in bytes (default 2 GB). Files at/above ~128 MB are indexed
    /// by streaming (bounded memory), so a high cap does not blow up RAM; the cost of a high cap is
    /// read time on a full build (large files are re-read), which is why it can be lowered per-repo.</summary>
    public static long MaxFileBytes() => MaxFileBytes(_current);
    public static long MaxFileBytes(RepoConfig cfg)
    {
        long? mb = EnvLong("CODECOMPASS_MAX_FILE_MB") ?? cfg.MaxFileMb;
        return mb is > 0 ? mb.Value * 1024 * 1024 : 2000L * 1024 * 1024;
    }

    /// <summary>Symbol-extraction size cap in characters (~bytes for ASCII; default 1 MB).</summary>
    public static int MaxSymbolChars() => MaxSymbolChars(_current);
    public static int MaxSymbolChars(RepoConfig cfg)
    {
        long? mb = EnvLong("CODECOMPASS_MAX_SYMBOL_MB") ?? cfg.MaxSymbolMb;
        long chars = (mb is > 0 ? mb.Value : 1) * 1024 * 1024;
        return chars > int.MaxValue ? int.MaxValue : (int)chars;
    }

    /// <summary>MCP auto-index limit in bytes; larger workspaces are left for a CLI build (default 100 MB).</summary>
    public static long MaxAutoBytes() => MaxAutoBytes(_current);
    public static long MaxAutoBytes(RepoConfig cfg)
    {
        // 0 is valid here (forces CLI build), so distinguish "set" from "absent".
        long? mb = EnvLong("CODECOMPASS_MAX_AUTO_MB") ?? cfg.MaxAutoMb;
        return (mb is >= 0 ? mb.Value : 100) * 1024 * 1024;
    }

    /// <summary>Indexing parallelism (default: all cores).</summary>
    public static int Threads(int defaultCores) => Threads(_current, defaultCores);
    public static int Threads(RepoConfig cfg, int defaultCores)
    {
        int? n = EnvInt("CODECOMPASS_THREADS") ?? cfg.Threads;
        return n is > 0 ? n.Value : defaultCores;
    }

    /// <summary>Explicit per-worker text-segment budget in MB, or null to size adaptively.</summary>
    public static int? SegmentMbOverride() => SegmentMbOverride(_current);
    public static int? SegmentMbOverride(RepoConfig cfg)
    {
        int? mb = EnvInt("CODECOMPASS_SEGMENT_MB") ?? cfg.SegmentMb;
        return mb is > 0 ? mb : null;
    }

    /// <summary>Segment count that triggers compaction on the incremental path (default 64).</summary>
    public static int CompactSegments() => CompactSegments(_current);
    public static int CompactSegments(RepoConfig cfg)
    {
        int? n = EnvInt("CODECOMPASS_COMPACT_SEGMENTS") ?? cfg.CompactSegments;
        return n is >= 2 ? n.Value : 64;
    }

    /// <summary>
    /// Total file bytes allowed in flight during a parallel build. Bounds RAM when the file cap is
    /// large: without it, every core could read a multi-GB file at once and blow up memory. Scales
    /// with machine RAM (~1/16 of it, clamped 256 MB..4 GB); a single file bigger than the budget is
    /// read alone. Override with CODECOMPASS_READ_BUDGET_MB / config readBudgetMb.
    /// </summary>
    public static long ReadBudgetBytes() => ReadBudgetBytes(_current);
    public static long ReadBudgetBytes(RepoConfig cfg)
    {
        long? mb = EnvLong("CODECOMPASS_READ_BUDGET_MB") ?? cfg.ReadBudgetMb;
        if (mb is > 0) return mb.Value * 1024 * 1024;
        long avail;
        try { avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { avail = 8L * 1024 * 1024 * 1024; }
        if (avail <= 0) avail = 8L * 1024 * 1024 * 1024;
        return Math.Clamp(avail / 16, 256L * 1024 * 1024, 4L * 1024 * 1024 * 1024);
    }

    /// <summary>Seconds of no indexing progress before a stall warning (default 60).</summary>
    public static int StallWarnSec() => StallWarnSec(_current);
    public static int StallWarnSec(RepoConfig cfg)
    {
        int? n = EnvInt("CODECOMPASS_STALL_WARN_SEC") ?? cfg.StallWarnSec;
        return n is >= 5 ? n.Value : 60;
    }

    /// <summary>Extra directory names to skip: the union of CODECOMPASS_IGNORE and the config file.</summary>
    public static IEnumerable<string> IgnoredDirs() => IgnoredDirs(_current);
    public static IEnumerable<string> IgnoredDirs(RepoConfig cfg)
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_IGNORE");
        if (!string.IsNullOrWhiteSpace(env))
            foreach (var d in env.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return d;
        if (cfg.Ignore is not null)
            foreach (var d in cfg.Ignore)
                if (!string.IsNullOrWhiteSpace(d)) yield return d.Trim();
    }
}
