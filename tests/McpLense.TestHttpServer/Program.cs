using System.ComponentModel;
using McpLense.TestServer.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpLense.TestHttpServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var urlFile = ParseUrlFile(args);

        using var app = await StartAsync(urlFile);
        await app.WaitForShutdownAsync();
    }

    /// <summary>
    /// Builds and starts the HTTP MCP test server bound to <c>http://127.0.0.1:0</c>
    /// (an OS-assigned port). Suitable for both subprocess and in-process hosting in tests.
    /// When <paramref name="urlFile"/> is provided, the bound base URL is written to that file
    /// once the server is listening.
    /// </summary>
    public static async Task<WebApplication> StartAsync(string? urlFile = null)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Warning;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddHttpContextAccessor();

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
                // Stateful + legacy SSE so a single instance can satisfy
                // auto, streamable-http, and SSE transport tests.
                options.Stateless = false;
#pragma warning disable MCP9004
                options.EnableLegacySse = true;
#pragma warning restore MCP9004
            })
            .WithTools<EchoTools>()
            .WithTools<MathTools>()
            .WithTools<LongRunningTools>()
            .WithTools<FailingTools>()
            .WithTools<HeaderTools>()
            .WithResources<TestResources>()
            .WithPrompts<TestPrompts>();

        var app = builder.Build();
        app.MapMcp();

        await app.StartAsync();

        if (!string.IsNullOrWhiteSpace(urlFile))
        {
            var baseUrl = app.Urls.First();
            var directory = Path.GetDirectoryName(Path.GetFullPath(urlFile));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(urlFile, baseUrl);
        }

        return app;
    }

    private static string? ParseUrlFile(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--url-file" && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

[McpServerToolType]
public sealed class HeaderTools
{
    [McpServerTool(Name = "GetHeader"), Description("Returns the value of an inbound HTTP request header.")]
    public static string GetHeader(
        IHttpContextAccessor httpContextAccessor,
        [Description("Header name to read")] string name)
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        if (headers is null)
        {
            return "<no-context>";
        }

        return headers.TryGetValue(name, out var value)
            ? value.ToString()
            : "<missing>";
    }
}
