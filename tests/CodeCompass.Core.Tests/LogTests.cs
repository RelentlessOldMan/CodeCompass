using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCompass.Core.Diagnostics;
using Xunit;

namespace CodeCompass.Core.Tests;

// These tests drive the logger through process-global env vars, so they must not run
// concurrently with each other.
[Collection("log-env")]
public class LogTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly List<(string Key, string? Old)> _saved = new();
        public EnvScope(params (string Key, string? Val)[] vars)
        {
            foreach (var (k, v) in vars)
            {
                _saved.Add((k, Environment.GetEnvironmentVariable(k)));
                Environment.SetEnvironmentVariable(k, v);
            }
        }
        public void Dispose()
        {
            foreach (var (k, old) in _saved) Environment.SetEnvironmentVariable(k, old);
        }
    }

    [Fact]
    public void DefaultLevel_WritesInfo_DropsDebug()
    {
        var dir = NewTempDir();
        try
        {
            using var _ = new EnvScope(
                ("CODECOMPASS_LOG_DIR", dir), ("CODECOMPASS_LOG", null), ("CODECOMPASS_LOG_LEVEL", null));
            var path = Path.Combine(dir, "lvl.log");
            var log = new DiskLogger(path, "test", null);

            log.Info("visible-info");
            log.Debug("hidden-debug");

            var text = File.ReadAllText(path);
            Assert.Contains("visible-info", text);
            Assert.DoesNotContain("hidden-debug", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LogOff_WritesNothing()
    {
        var dir = NewTempDir();
        try
        {
            using var _ = new EnvScope(("CODECOMPASS_LOG_DIR", dir), ("CODECOMPASS_LOG", "0"));
            var path = Path.Combine(dir, "off.log");
            var log = new DiskLogger(path, "test", null);

            log.Error("should-not-appear");

            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Mirror_ReceivesWarnAndError_NotInfo()
    {
        var dir = NewTempDir();
        try
        {
            using var _ = new EnvScope(
                ("CODECOMPASS_LOG_DIR", dir), ("CODECOMPASS_LOG", null), ("CODECOMPASS_LOG_LEVEL", "info"));
            var commonPath = Path.Combine(dir, "common.log");
            var repoPath = Path.Combine(dir, "repo.log");
            var common = new DiskLogger(commonPath, "common", null);
            var repo = new DiskLogger(repoPath, "repo", common);

            repo.Info("routine-info");
            repo.Warn("a-warning");
            repo.Error("an-error");

            var repoText = File.ReadAllText(repoPath);
            Assert.Contains("routine-info", repoText);
            Assert.Contains("a-warning", repoText);
            Assert.Contains("an-error", repoText);

            var commonText = File.ReadAllText(commonPath);
            Assert.DoesNotContain("routine-info", commonText); // info stays local
            Assert.Contains("a-warning", commonText);          // problems bubble up
            Assert.Contains("an-error", commonText);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Rotation_BoundsFileCountAndSize()
    {
        var dir = NewTempDir();
        try
        {
            using var _ = new EnvScope(
                ("CODECOMPASS_LOG_DIR", dir), ("CODECOMPASS_LOG", null), ("CODECOMPASS_LOG_LEVEL", "debug"),
                ("CODECOMPASS_LOG_MAX_MB", "1"), ("CODECOMPASS_LOG_KEEP", "2"));
            var path = Path.Combine(dir, "rot.log");
            var log = new DiskLogger(path, "test", null);

            var big = new string('x', 500);
            for (int i = 0; i < 12000; i++) log.Info(big); // well over 1 MB -> several rotations

            // keep=2 => at most rot.log + rot.log.1 + rot.log.2; never rot.log.3
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".3"));

            long max = 1L * 1024 * 1024;
            foreach (var f in new DirectoryInfo(dir).GetFiles("rot.log*"))
                Assert.True(f.Length <= max + 4096, $"{f.Name} is {f.Length} bytes, over the cap");

            // total across the family stays bounded (< max * (keep+1) + slack)
            long total = new DirectoryInfo(dir).GetFiles("rot.log*").Sum(f => f.Length);
            Assert.True(total <= max * 3 + 8192, $"total {total} bytes exceeds bound");
        }
        finally { Directory.Delete(dir, true); }
    }
}
