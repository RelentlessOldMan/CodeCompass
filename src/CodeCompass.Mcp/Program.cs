using CodeCompass.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The repository this server serves: first non-flag arg, else the current directory.
// Repository to serve: first non-flag arg if it's a real directory, else the cwd.
// (This tolerates an unexpanded ${CLAUDE_PROJECT_DIR} when launched as a plugin.)
var argRoot = args.FirstOrDefault(a => !a.StartsWith('-'));
var root = !string.IsNullOrEmpty(argRoot) && Directory.Exists(argRoot)
    ? argRoot
    : Directory.GetCurrentDirectory();
ServerContext.Init(root);
ServerContext.EnableLiveIndex(); // keep the index fresh as files change

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP (JSON-RPC) transport, so all logging must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
