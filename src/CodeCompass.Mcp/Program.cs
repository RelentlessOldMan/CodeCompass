using CodeCompass.Core.Diagnostics;
using CodeCompass.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Opt out of EcoQoS throttling so background auto-reindex isn't parked on E-cores. No priority
// nudge: this is a long-lived server that shouldn't outrank the editor/agent it shares the box with.
ProcessPerformance.RequestFullSpeed(raisePriority: false);

// The repository this server serves: first non-flag arg, else the current directory.
// Repository to serve: first non-flag arg if it's a real directory, else the cwd.
// (This tolerates an unexpanded ${CLAUDE_PROJECT_DIR} when launched as a plugin.)
var argRoot = args.FirstOrDefault(a => !a.StartsWith('-'));
var root = !string.IsNullOrEmpty(argRoot) && Directory.Exists(argRoot)
    ? argRoot
    : Directory.GetCurrentDirectory();

Log.Global.Info($"mcp server v{BuildInfo.Version} starting, root={root}");
Log.For(root).Info("mcp server attached to this repo");

ServerContext.Init(root);
ServerContext.EnableLiveIndex(); // keep the index fresh as files change

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdout is the MCP (JSON-RPC) transport, so all logging must go to stderr.
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Global.Error("mcp server terminated with an unhandled exception", ex);
    throw;
}
