using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CodeCompass.Mcp;

/// <summary>The server's host builder. The Generic Host's defaults root it at the cwd - usually the repo itself - and load
/// <c>appsettings*.json</c> from there with reload-on-change, which keeps a recursive file watcher on the whole tree in every
/// session (every build output, every sync, over a share too) for config this server never has. Rooted at the install
/// directory with no reload instead: no watcher, and a repo's own appsettings.json can't configure this server.</summary>
internal static class McpHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection([new("hostBuilder:reloadConfigOnChange", "false")]);
        return Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            Configuration = config,
        });
    }
}
