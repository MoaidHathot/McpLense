using McpLense.TestServer.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpLense.TestServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Warning;
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<EchoTools>()
            .WithTools<MathTools>()
            .WithTools<LongRunningTools>()
            .WithTools<FailingTools>()
            .WithResources<TestResources>()
            .WithPrompts<TestPrompts>();

        await builder.Build().RunAsync();
    }
}
