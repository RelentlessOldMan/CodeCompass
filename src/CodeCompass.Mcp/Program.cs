using CodeCompass.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The repository this server serves: first non-flag arg, else the current directory.
var root = args.FirstOrDefault(a => !a.StartsWith('-')) ?? Directory.GetCurrentDirectory();
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
