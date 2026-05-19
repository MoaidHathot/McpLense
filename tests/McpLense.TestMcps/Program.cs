using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpLense.TestMcps;

/// <summary>
/// Single-binary test MCP host that boots one of several flavours selected via
/// <c>--mode &lt;bare|rich|sampling|leaky|headers&gt;</c>. Used by integration tests as
/// deterministic fixtures for the scan pipeline: each mode demonstrates one behaviour the
/// scanner cares about (long instructions, mixed annotations, server-initiated calls,
/// leaky errors, header echoing).
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var mode = ParseArg(args, "--mode") ?? "bare";
        var urlFile = ParseArg(args, "--url-file");

        using var app = await StartAsync(mode, urlFile);
        await app.WaitForShutdownAsync();
    }

    /// <summary>
    /// Builds and starts the chosen test MCP on <c>http://127.0.0.1:0</c> (OS-assigned port).
    /// </summary>
    public static async Task<WebApplication> StartAsync(string mode, string? urlFile = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Warning);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Per-server header capture: keyed by HTTP method + path so a single test process
        // can drill into the headers the MCP server saw for each leg of the scan
        // (initialize POST, transport-probe GET, CORS preflight OPTIONS, etc.).
        var headerCapture = new HeaderCapture();
        builder.Services.AddSingleton(headerCapture);

        var mcp = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = ResolveServerInfo(mode);
                options.ServerInstructions = ResolveServerInstructions(mode);
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = false;
            });

        switch (mode.ToLowerInvariant())
        {
            case "bare":
                mcp.WithTools<BareTools>();
                break;
            case "rich":
                mcp.WithTools<RichTools>();
                mcp.WithPrompts<RichPrompts>();
                mcp.WithResources<RichResources>();
                break;
            case "sampling":
                mcp.WithTools<BareTools>();
                break;
            case "leaky":
                mcp.WithTools<LeakyTools>();
                break;
            case "headers":
                // 'headers' mode registers the bare Echo tool to be a valid MCP, but the
                // actual test surface is the /capture endpoint added below.
                mcp.WithTools<BareTools>();
                break;
            default:
                throw new ArgumentException($"Unknown --mode value '{mode}'. Allowed: bare, rich, sampling, leaky, headers.");
        }

        var app = builder.Build();

        // LeakyMcp deliberately advertises Server / X-Powered-By headers so the scanner's
        // metrics + headers checks have something to capture. Other modes get the default
        // (no banner headers).
        if (mode.Equals("leaky", StringComparison.OrdinalIgnoreCase))
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Response.Headers["Server"] = "TestMcp/1.0+gitsha";
                ctx.Response.Headers["X-Powered-By"] = "leaky-mcp";
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                await next();
            });
        }

        // Capture middleware for the 'headers' mode: every inbound request appends its
        // method + path + headers to the shared HeaderCapture. The EchoHeaders tool reads
        // the capture and returns it as a string. Two endpoints get special handling:
        //   /capture - GET returns a JSON snapshot of every request seen so far. Used by
        //              tests that prefer querying the capture out-of-band instead of via
        //              a tool call.
        //   /capture/clear - POST resets the capture buffer.
        if (mode.Equals("headers", StringComparison.OrdinalIgnoreCase))
        {
            app.Use(async (ctx, next) =>
            {
                headerCapture.Record(ctx);
                await next();
            });

            app.MapGet("/capture", (HeaderCapture cap) => Results.Json(cap.SnapshotJson()));
            app.MapPost("/capture/clear", (HeaderCapture cap) => { cap.Clear(); return Results.Ok(); });
        }

        app.MapMcp();
        await app.StartAsync();

        if (!string.IsNullOrEmpty(urlFile))
        {
            var baseUrl = app.Urls.First();
            var dir = Path.GetDirectoryName(Path.GetFullPath(urlFile));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(urlFile, baseUrl);
        }

        return app;
    }

    private static ModelContextProtocol.Protocol.Implementation ResolveServerInfo(string mode)
        => new()
        {
            Name = $"TestMcp.{mode}",
            Title = mode switch
            {
                "rich" => "Rich Test MCP",
                "sampling" => "Sampling Test MCP",
                "leaky" => "Leaky Test MCP",
                "headers" => "Headers Test MCP",
                _ => "Bare Test MCP"
            },
            Version = "0.0.1-test",
            Description = mode switch
            {
                "rich" => "Rich test MCP with verbose instructions, prompts, and resources.",
                "sampling" => "Test MCP that attempts to use sampling immediately after init.",
                "leaky" => "Test MCP that leaks stack traces and server headers.",
                "headers" => "Test MCP that records inbound HTTP headers for assertion-driven tests.",
                _ => "Minimal bare-bones test MCP."
            },
            WebsiteUrl = "https://example.invalid/test-mcps"
        };

    private static string? ResolveServerInstructions(string mode) => mode switch
    {
        "bare" => null,
        "rich" =>
            "You are connected to a rich test MCP that exists to exercise mcplense's metrics " +
            "and hashing checks.\n\nUse the available tools for tasks involving math, text " +
            "manipulation, or sample resource fetching. Visit https://example.invalid/docs " +
            "for documentation, or https://example.invalid/issues for known issues.\n\n" +
            "```python\n# example call\nresult = tools.add(1, 2)\n```\n\n" +
            "Do not use this server for production work - it serves test fixtures only.",
        "sampling" =>
            "This server demonstrates server-initiated sampling. After init it will request " +
            "an LLM completion from the host via sampling/createMessage.",
        "leaky" =>
            "Leaky test server. Tools deliberately throw with stack traces in the response.",
        "headers" =>
            "Test MCP that records inbound headers. Call the EchoHeaders tool to retrieve them.",
        _ => null
    };

    private static string? ParseArg(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == key)
            {
                return args[i + 1];
            }
        }
        return null;
    }
}

/// <summary>
/// Records inbound HTTP requests for the <c>headers</c> mode. Thread-safe so concurrent
/// probe requests can all append without locking each other out.
/// </summary>
public sealed class HeaderCapture
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    public void Record(HttpContext ctx)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in ctx.Request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value.ToArray());
        }
        _requests.Enqueue(new CapturedRequest(
            Method: ctx.Request.Method,
            Path: ctx.Request.Path.Value ?? string.Empty,
            Headers: headers));
    }

    public IReadOnlyList<CapturedRequest> Snapshot() => _requests.ToArray();

    public object SnapshotJson() => new
    {
        requests = _requests.Select(r => new
        {
            method = r.Method,
            path = r.Path,
            headers = r.Headers
        }).ToArray()
    };

    public void Clear()
    {
        while (_requests.TryDequeue(out _)) { }
    }
}

public sealed record CapturedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

[McpServerToolType]
public sealed class BareTools
{
    [McpServerTool(Name = "Echo", ReadOnly = true, Idempotent = true), Description("Echoes the input verbatim.")]
    public static string Echo([Description("The string to echo back.")] string message) => message;
}

[McpServerToolType]
public sealed class RichTools
{
    // Mixed annotations: some declared, some missing. Exercises Tier 1 'missingAnnotations'.
    [McpServerTool(Name = "Add", ReadOnly = true, Idempotent = true), Description("Adds two integers.")]
    public static int Add(int a, int b) => a + b;

    [McpServerTool(Name = "DeleteFile", Destructive = true), Description("Deletes a file at the given path (stub).")]
    public static string DeleteFile([Description("Absolute path to delete (no-op in tests).")] string path)
        => $"would-delete: {path}";

    // Intentionally NO annotations declared - lets the test assert missingAnnotations is
    // the full set ["readOnlyHint","destructiveHint","idempotentHint","openWorldHint"].
    [McpServerTool(Name = "Mystery"), Description("A tool with no declared annotations. Behaviour TBD.")]
    public static string Mystery(string input) => $"mystery({input})";
}

[McpServerToolType]
public sealed class LeakyTools
{
    [McpServerTool(Name = "Crash"), Description("Throws a deliberately leaky exception.")]
    public static string Crash(string input)
    {
        throw new InvalidOperationException(
            $"Internal failure at C:\\internal\\paths\\test.cs:42 processing '{input}' (debug: build=12345, host=internal-prod-srv01)");
    }
}

public sealed class RichPrompts
{
    [McpServerPrompt(Name = "summarize"), Description("Summarize a block of text.")]
    public static string Summarize(
        [Description("Text to summarize")] string text,
        [Description("Max length in words")] int? max = 100)
        => $"Summarize this in {max} words: {text}";
}

public sealed class RichResources
{
    [McpServerResource(UriTemplate = "test://resource/{id}", Name = "TestResource"), Description("Synthetic test resource keyed by id.")]
    public static string ReadResource(string id) => $"test-resource-body-{id}";
}
