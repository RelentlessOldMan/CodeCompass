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
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
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

    // Each knob has a pure overload taking an explicit RepoConfig (deterministic; used by tests and
    // tools) and an ambient no-arg overload that resolves against the active repo config.

    /// <summary>Per-file indexing size cap in bytes (default 5 MB).</summary>
    public static long MaxFileBytes() => MaxFileBytes(_current);
    public static long MaxFileBytes(RepoConfig cfg)
    {
        long? mb = EnvLong("CODECOMPASS_MAX_FILE_MB") ?? cfg.MaxFileMb;
        return mb is > 0 ? mb.Value * 1024 * 1024 : 5L * 1024 * 1024;
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
