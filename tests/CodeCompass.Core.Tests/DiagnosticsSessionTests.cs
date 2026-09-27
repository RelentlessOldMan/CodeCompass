using System;
using System.Diagnostics;
using System.IO;
using CodeCompass.Core.Diagnostics;
using Xunit;

namespace CodeCompass.Core.Tests;

// Session correlation + abnormal-shutdown detection (diagnostic-system spec). These exercise the logic
// directly (not the process-global DiagnosticsSession.Start, which installs real handlers) and isolate the
// marker directory via CODECOMPASS_LOG_DIR.
public class DiagnosticsSessionTests
{
    [Fact]
    public void SessionId_IsStable_AndStampedOnEveryLogLine()
    {
        Assert.False(string.IsNullOrWhiteSpace(Log.SessionId));
        Assert.Equal(Log.SessionId, Log.SessionId); // one id for the life of the process

        var tmp = Path.Combine(Path.GetTempPath(), "cc-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var logger = new DiskLogger(tmp, "test-component", mirror: null);
            logger.Info("hello diagnostics");

            var text = File.ReadAllText(tmp);
            Assert.Contains($"/{Log.SessionId}]", text); // the [pid/session] correlation stamp
            Assert.Contains("hello diagnostics", text);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void AbnormalPriorSession_IsReportedAndCleared_WhileLiveSessionIsKept()
    {
        var prevDir = Environment.GetEnvironmentVariable("CODECOMPASS_LOG_DIR");
        var tmp = Path.Combine(Path.GetTempPath(), "cc-sess-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_LOG_DIR", tmp);
            var dir = Path.Combine(tmp, "sessions");
            Directory.CreateDirectory(dir);

            // A dead session: a pid that cannot be alive -> its marker is stale and must be reported+cleared.
            var deadMarker = Path.Combine(dir, "deadbeef.active");
            File.WriteAllText(deadMarker, $"{int.MaxValue}|{DateTime.UtcNow.Ticks}|mcp|deadbeef");

            // A live session: THIS process (matching pid + start time) must be recognized as alive and kept.
            using var me = Process.GetCurrentProcess();
            var liveMarker = Path.Combine(dir, "alive001.active");
            File.WriteAllText(liveMarker, $"{me.Id}|{me.StartTime.ToUniversalTime().Ticks}|mcp|alive001");

            int abnormal = DiagnosticsSession.ReportAbnormalPriorSessions();

            Assert.Equal(1, abnormal);
            Assert.False(File.Exists(deadMarker), "a dead session's marker must be cleared after it is reported");
            Assert.True(File.Exists(liveMarker), "a still-running session's marker must be preserved");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_LOG_DIR", prevDir);
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void ReportAbnormalPriorSessions_NoMarkersDir_ReturnsZero_NoThrow()
    {
        var prevDir = Environment.GetEnvironmentVariable("CODECOMPASS_LOG_DIR");
        var tmp = Path.Combine(Path.GetTempPath(), "cc-sess-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_LOG_DIR", tmp); // no sessions/ subdir exists
            Assert.Equal(0, DiagnosticsSession.ReportAbnormalPriorSessions());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODECOMPASS_LOG_DIR", prevDir);
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
