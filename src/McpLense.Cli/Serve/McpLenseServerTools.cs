using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpLense;

/// <summary>
/// The tools McpLense exposes when run as an MCP server (<c>mcplense serve</c>): they let an agent
/// introspect and audit OTHER MCP servers. Each tool reuses the exact CLI pipeline
/// (<see cref="CommandLineParser"/> -&gt; <see cref="McpExecutor"/>) and returns the JSON report, so
/// the server behaves identically to the command line.
/// </summary>
[McpServerToolType]
internal sealed class McpLenseServerTools
{
    [McpServerTool(Name = "mcplense_inspect")]
    [Description("Inspect an MCP server: list its tools, resources, and prompts. Returns the JSON inspect report.")]
    public static Task<string> Inspect([Description("The MCP server URL, e.g. https://host/mcp")] string url, CancellationToken cancellationToken)
        => RunAsync(["inspect", url, "--quiet"], cancellationToken);

    [McpServerTool(Name = "mcplense_scan")]
    [Description("Run McpLense's fact-only posture scan against an MCP server. Returns the JSON report (auth model, TLS, capabilities, tool annotations, behaviour).")]
    public static Task<string> Scan([Description("The MCP server URL")] string url, CancellationToken cancellationToken)
        => RunAsync(["scan", url, "--quiet"], cancellationToken);

    [McpServerTool(Name = "mcplense_analyze")]
    [Description("Scan an MCP server and return severity-rated security findings (prompt-injection signals, anonymous destructive tools, weak CORS, TLS posture, ...).")]
    public static Task<string> Analyze([Description("The MCP server URL")] string url, CancellationToken cancellationToken)
        => RunAsync(["analyze", url, "--quiet"], cancellationToken);

    [McpServerTool(Name = "mcplense_explain")]
    [Description("Return a short plain-language explanation of what an MCP server is and whether it looks safe.")]
    public static Task<string> Explain([Description("The MCP server URL")] string url, CancellationToken cancellationToken)
        => RunAsync(["explain", url, "--quiet"], cancellationToken);

    private static async Task<string> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var command = CommandLineParser.Parse(args);
            var outcome = await McpExecutor.ExecuteAsync(command, App.JsonOptions, cancellationToken).ConfigureAwait(false);
            return OutputRenderer.Render(OutputFormat.Json, outcome.Payload, App.JsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"{ex.GetType().Name}: {ex.Message}" }, App.JsonOptions);
        }
    }
}
