using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpLense;

/// <summary>
/// <c>mcplense serve</c>: runs McpLense itself as an MCP server over stdio, exposing
/// inspect/scan/analyze/explain as tools (see <see cref="McpLenseServerTools"/>) so an agent can
/// introspect and audit other MCP servers. All logging is routed to stderr to keep stdout clean for
/// the MCP protocol stream. Runs until the host (the calling agent) closes the connection.
/// </summary>
internal static class ServeCommand
{
    public static async Task<int> RunAsync(ParsedCommand command)
    {
        var builder = Host.CreateApplicationBuilder();

        // The MCP stdio transport owns stdout; every log line must go to stderr or it corrupts the
        // protocol stream. Clear the default providers and add a stderr-only console logger.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<McpLenseServerTools>();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }
}
