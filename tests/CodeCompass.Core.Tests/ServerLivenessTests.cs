using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeCompass.Core.Hooks;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

// The Grep hook redirected whenever an index existed on disk - also in a session whose CodeCompass server had failed to
// connect (or been stopped), leaving the agent with Grep denied and no search_code to use. A serving server now leaves
// a liveness marker for its root, and the hook redirects only while one is live.
public class ServerLivenessTests
{
    [Fact]
    public void NoServer_IsNotServed()
    {
        using var repo = new TempRepo();
        Assert.False(ServerLiveness.IsServed(repo.Root));
    }

    [Fact]
    public void Marked_IsServed_UntilDisposed()
    {
        using var repo = new TempRepo();
        var mark = ServerLiveness.Mark(repo.Root);
        Assert.NotNull(mark);
        Assert.True(ServerLiveness.IsServed(repo.Root));
        mark!.Dispose();
        Assert.False(ServerLiveness.IsServed(repo.Root));
    }

    [Fact]
    public void AMarkerLeftByADeadProcess_IsNotServed()
    {
        using var repo = new TempRepo();
        var dir = Path.Combine(IndexStore.CacheDirPath(repo.Root), "serving");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "2147483000.alive"), ""); // no such process
        Assert.False(ServerLiveness.IsServed(repo.Root));
    }

    // A marker can outlive its process (a power loss, a BSOD, a kill where delete-on-close is emulated), and after a reboot
    // its pid can belong to an unrelated process: counted, the hook denied Grep in a session with no server. Only a marker
    // a process holds open counts - and one nobody holds is cleaned up.
    [Fact]
    public void AMarkerNobodyHolds_IsNotServed_EvenIfItsPidIsRunning_AndIsRemoved()
    {
        using var repo = new TempRepo();
        var dir = Path.Combine(IndexStore.CacheDirPath(repo.Root), "serving");
        Directory.CreateDirectory(dir);
        var stale = Path.Combine(dir, System.Environment.ProcessId + ".alive"); // a running pid: ours
        File.WriteAllText(stale, "");
        Assert.False(ServerLiveness.IsServed(repo.Root));
        Assert.False(File.Exists(stale));
    }

    // A live server's marker must survive another process's cleanup: shared for delete, a probe that misjudged it (an NFS home
    // on Linux takes no shared lock for it) deleted it, and the redirect went silently off for the server's lifetime.
    [Fact]
    public void ALiveMarker_IsHeldExclusively()
    {
        using var repo = new TempRepo();
        using var mark = ServerLiveness.Mark(repo.Root);
        Assert.NotNull(mark);
        var file = Directory.GetFiles(Path.Combine(IndexStore.CacheDirPath(repo.Root), "serving"), "*.alive").Single();
        Assert.ThrowsAny<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose());
        if (OperatingSystem.IsWindows()) Assert.ThrowsAny<IOException>(() => File.Delete(file));
        Assert.True(ServerLiveness.IsServed(repo.Root));
        Assert.True(File.Exists(file));
    }

    // A server that can't read the index (written by a newer CodeCompass) has no search_code answer to send Grep to.
    [Fact]
    public void AServerThatCantReadTheIndex_LetsGrepRun()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { }");
        var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
        t0.Dispose(); s0.Dispose();
        var seg = Directory.GetFiles(IndexStore.CacheDirPath(repo.Root), "seg-*.ccseg")[0];
        using (var fs = new FileStream(seg, FileMode.Open, FileAccess.Write)) { fs.Position = 4; fs.Write(System.BitConverter.GetBytes(99)); }
        CodeCompass.Mcp.ServerContext.Init(repo.Root);
        try
        {
            Assert.True(ServerLiveness.IsServed(repo.Root)); // a cold start counts: the index loads on the first query
            Assert.Contains("NEWER CodeCompass", CodeCompass.Mcp.CodeCompassTools.SearchCode("class A"));
            Assert.False(ServerLiveness.IsServed(repo.Root));
        }
        finally { CodeCompass.Mcp.ServerContext.Init(repo.Root); }
    }

    [Fact]
    public void Server_MarksItsRoot_AndMovesTheMarkOnRePoint()
    {
        using var a = new TempRepo();
        using var b = new TempRepo();
        CodeCompass.Mcp.ServerContext.Init(a.Root);
        try
        {
            Assert.True(ServerLiveness.IsServed(a.Root));
            CodeCompass.Mcp.ServerContext.Init(a.Root); // re-point to the SAME root keeps it marked
            Assert.True(ServerLiveness.IsServed(a.Root));
            CodeCompass.Mcp.ServerContext.Init(b.Root);
            Assert.False(ServerLiveness.IsServed(a.Root));
            Assert.True(ServerLiveness.IsServed(b.Root));
        }
        finally { CodeCompass.Mcp.ServerContext.Init(a.Root); }
    }

    [Fact]
    public void Hook_IndexedButNoServer_PassesThroughToGrep_ThenRedirectsOnceServed()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class Widget { }\n");
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse", tool_name = "Grep", cwd = repo.Root,
            tool_input = new { pattern = "Widget" },
        });

        Assert.False(HookPayloads.ShouldRedirectGrep(payload, repo.Root));
        using (ServerLiveness.Mark(repo.Root))
            Assert.True(HookPayloads.ShouldRedirectGrep(payload, repo.Root));
    }
}
