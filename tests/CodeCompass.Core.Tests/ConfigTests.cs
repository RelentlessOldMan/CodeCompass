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
}
