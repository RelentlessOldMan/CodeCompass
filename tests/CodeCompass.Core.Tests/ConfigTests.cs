using System;
using System.Linq;
using CodeCompass.Core.Config;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// Shares the CODECOMPASS_* env vars with the other cap tests, so it joins their collection to run
// serially (env is process-global). The parse/precedence tests use the pure RepoConfig-taking
// overloads, so they don't depend on the ambient active-config that parallel indexing mutates.
[Collection("symbolcap-env")]
public class ConfigTests
{
    [Fact]
    public void ReadFrom_ParsesFields()
    {
        using var repo = new TempRepo();
        repo.Write(".codecompass.json",
            """{ "maxSymbolMb": 4, "maxFileMb": 8, "ignore": ["chipreg", "generated"] }""");

        var cfg = CodeCompassConfig.ReadFrom(repo.Root);
        Assert.NotNull(cfg);
        Assert.Equal(4, cfg!.MaxSymbolMb);
        Assert.Equal(8, cfg.MaxFileMb);
        Assert.Equal(new[] { "chipreg", "generated" }, cfg.Ignore);
    }

    [Fact]
    public void Template_ParsesToAllDefaults()
    {
        // The `codecompass init` starter has every setting commented out, so an unedited copy must
        // parse cleanly (comments/trailing commas tolerated) to an all-null config == all defaults.
        using var repo = new TempRepo();
        repo.Write(".codecompass.json", CodeCompassConfig.Template);

        var cfg = CodeCompassConfig.ReadFrom(repo.Root);
        Assert.NotNull(cfg);
        Assert.Null(cfg!.MaxSymbolMb);
        Assert.Null(cfg.MaxFileMb);
        Assert.Null(cfg.Ignore);
        Assert.Null(cfg.Threads);
        Assert.Null(cfg.ReadBudgetMb);
    }

    [Fact]
    public void ReadFrom_InvalidJson_ReturnsNull_NoThrow()
    {
        using var repo = new TempRepo();
        repo.Write(".codecompass.json", "{ this is not valid json ");
        Assert.Null(CodeCompassConfig.ReadFrom(repo.Root));
    }

    [Fact]
    public void ReadFrom_NoFile_ReturnsNull()
    {
        using var repo = new TempRepo();
        Assert.Null(CodeCompassConfig.ReadFrom(repo.Root));
    }

    [Fact]
    public void Precedence_Env_Then_File_Then_Default()
    {
        var fileCfg = new RepoConfig { MaxSymbolMb = 3 };

        Assert.Equal(1 * 1024 * 1024, CodeCompassConfig.MaxSymbolChars(new RepoConfig())); // default
        Assert.Equal(3 * 1024 * 1024, CodeCompassConfig.MaxSymbolChars(fileCfg));          // config file

        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", "7");
            Assert.Equal(7 * 1024 * 1024, CodeCompassConfig.MaxSymbolChars(fileCfg)); // env wins over file
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_SYMBOL_MB", old); }
    }

    // Nulls the CODECOMPASS_* knobs the resolver reads FIRST (env > file > default), so a value inherited
    // from the ambient environment can't mask the config-file/default behavior under test.
    private sealed class EnvNull : IDisposable
    {
        private readonly (string Key, string? Old)[] _saved;
        public EnvNull(params string[] keys)
        {
            _saved = keys.Select(k => (k, Environment.GetEnvironmentVariable(k))).ToArray();
            foreach (var k in keys) Environment.SetEnvironmentVariable(k, null);
        }
        public void Dispose() { foreach (var (k, old) in _saved) Environment.SetEnvironmentVariable(k, old); }
    }

    // The tuning-knob resolvers each guard their range; a dropped guard would silently poison a cap. These
    // pin the out-of-range/zero/overflow behavior the happy-path precedence tests never touch.
    [Fact]
    public void MaxSymbolChars_ClampsZeroAndNegativeToDefault_AndAvoidsOverflow()
    {
        using var _ = new EnvNull("CODECOMPASS_MAX_SYMBOL_MB");
        Assert.Equal(1 * 1024 * 1024, CodeCompassConfig.MaxSymbolChars(new RepoConfig { MaxSymbolMb = 0 }));  // 0 -> default
        Assert.Equal(1 * 1024 * 1024, CodeCompassConfig.MaxSymbolChars(new RepoConfig { MaxSymbolMb = -5 })); // negative -> default
        // A huge MB value must saturate at int.MaxValue, never overflow-wrap to a negative char cap.
        Assert.Equal(int.MaxValue, CodeCompassConfig.MaxSymbolChars(new RepoConfig { MaxSymbolMb = 9_000_000 }));
    }

    [Fact]
    public void MaxAutoBytes_ZeroIsValidForceCli_NegativeIsDefault()
    {
        using var _ = new EnvNull("CODECOMPASS_MAX_AUTO_MB");
        Assert.Equal(0, CodeCompassConfig.MaxAutoBytes(new RepoConfig { MaxAutoMb = 0 }));            // 0 = force CLI (valid)
        Assert.Equal(100L * 1024 * 1024, CodeCompassConfig.MaxAutoBytes(new RepoConfig { MaxAutoMb = -1 })); // negative -> default 100 MB
    }

    [Fact]
    public void MaxFileBytes_ZeroAndNegative_FallBackToTwoGigDefault()
    {
        using var _ = new EnvNull("CODECOMPASS_MAX_FILE_MB");
        long twoGb = 2000L * 1024 * 1024;
        Assert.Equal(twoGb, CodeCompassConfig.MaxFileBytes(new RepoConfig { MaxFileMb = 0 }));
        Assert.Equal(twoGb, CodeCompassConfig.MaxFileBytes(new RepoConfig { MaxFileMb = -10 }));
    }

    [Fact]
    public void CompactSegments_BelowMinTwo_FallsBackToDefault()
    {
        using var _ = new EnvNull("CODECOMPASS_COMPACT_SEGMENTS");
        Assert.Equal(64, CodeCompassConfig.CompactSegments(new RepoConfig { CompactSegments = 1 }));  // below the min-2 guard
        Assert.Equal(64, CodeCompassConfig.CompactSegments(new RepoConfig { CompactSegments = -3 }));
        Assert.Equal(2, CodeCompassConfig.CompactSegments(new RepoConfig { CompactSegments = 2 }));   // exactly the floor is honored
    }

    [Fact]
    public void StallWarnSec_BelowMinFive_FallsBackToDefault()
    {
        using var _ = new EnvNull("CODECOMPASS_STALL_WARN_SEC");
        Assert.Equal(60, CodeCompassConfig.StallWarnSec(new RepoConfig { StallWarnSec = 3 }));  // below the min-5 guard
        Assert.Equal(5, CodeCompassConfig.StallWarnSec(new RepoConfig { StallWarnSec = 5 }));   // the floor is honored
    }

    [Fact]
    public void SemanticIdleMinutes_ZeroDisablesEviction_NegativeIsDefault()
    {
        using var _ = new EnvNull("CODECOMPASS_SEMANTIC_IDLE_MIN");
        Assert.Equal(0, CodeCompassConfig.SemanticIdleMinutes(new RepoConfig { SemanticIdleMinutes = 0 }));   // 0 = keep resident (valid)
        Assert.Equal(10, CodeCompassConfig.SemanticIdleMinutes(new RepoConfig { SemanticIdleMinutes = -1 })); // negative -> default 10
    }

    [Fact]
    public void IgnoredDirs_UnionsEnvAndConfig()
    {
        var cfg = new RepoConfig { Ignore = new[] { "fromfile" } };
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_IGNORE");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_IGNORE", "fromenv1; fromenv2");
            var dirs = CodeCompassConfig.IgnoredDirs(cfg).ToHashSet();
            Assert.Contains("fromfile", dirs);
            Assert.Contains("fromenv1", dirs);
            Assert.Contains("fromenv2", dirs);
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_IGNORE", old); }
    }

    [Fact]
    public void Survey_BucketsFilesByCap()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", "5"); // pin the cap (default is now 2 GB)
            using var repo = new TempRepo();
            repo.WriteBytes("small.cs", new byte[100]);                 // indexed, symbols
            repo.WriteBytes("gen.cs", new byte[(int)(1.2 * 1024 * 1024)]); // > 1 MB symbol cap -> symbol-skipped
            repo.WriteBytes("huge.cs", new byte[(int)(5.5 * 1024 * 1024)]); // > 5 MB file cap -> over-file-cap

            var r = Surveyor.Survey(repo.Root);
            Assert.Equal(2, r.IndexedFiles); // small + gen (huge is over the file cap)
            Assert.Contains(r.SymbolSkipped, x => x.Path == "gen.cs");
            Assert.DoesNotContain(r.SymbolSkipped, x => x.Path == "huge.cs");
            Assert.Contains(r.OverFileCap, x => x.Path == "huge.cs");
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", old); }
    }

    // Survey now walks through the shared FileWalker (no second hand-rolled traversal), so it prunes ignored
    // directories exactly like a build, and its over-file-cap list means "excluded by SIZE" only - an over-cap
    // file that's ALSO an ignored asset type (a big .png) is excluded because it's an asset, not the cap.
    [Fact]
    public void Survey_SharedWalk_PrunesIgnoredDirs_And_OverCapIsSizeOnly()
    {
        var old = Environment.GetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB");
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", "5"); // 5 MB file cap
            using var repo = new TempRepo();
            repo.WriteBytes("keep.cs", new byte[100]);                        // indexed
            repo.WriteBytes("node_modules/dep.js", new byte[100]);            // ignored dir -> excluded entirely
            repo.WriteBytes("big.png", new byte[(int)(6 * 1024 * 1024)]);     // over cap, but asset type -> NOT size-excluded
            repo.WriteBytes("big.cs", new byte[(int)(6 * 1024 * 1024)]);      // over cap, real code -> size-excluded

            var r = Surveyor.Survey(repo.Root);
            Assert.Equal(1, r.IndexedFiles);                                  // only keep.cs (node_modules pruned)
            Assert.Contains(r.OverFileCap, x => x.Path == "big.cs");          // excluded by SIZE
            Assert.DoesNotContain(r.OverFileCap, x => x.Path == "big.png");   // excluded by TYPE, not size
            Assert.DoesNotContain(r.OverFileCap, x => x.Path.Contains("node_modules"));
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_MAX_FILE_MB", old); }
    }
}
