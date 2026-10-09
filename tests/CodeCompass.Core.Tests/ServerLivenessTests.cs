using System.IO;
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
