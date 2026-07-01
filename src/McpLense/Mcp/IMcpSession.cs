using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace McpLense;

/// <summary>
/// A live MCP connection to a single resolved server, opened via
/// <see cref="McpExecutor.ConnectAsync"/>. Unlike the one-shot <see cref="McpExecutor.ExecuteAsync"/>
/// path (which connects and disconnects per operation), a session keeps the transport open so an
/// interactive caller can list completions, invoke a tool, read a resource and fetch a prompt over
/// the same connection - for stdio targets that means the server process stays up across calls.
/// </summary>
internal interface IMcpSession : IAsyncDisposable
{
    /// <summary>Identity of the connected server (name / transport / target).</summary>
    ServerReference Server { get; }

    /// <summary>
    /// Requests the server send log messages at or above <paramref name="level"/>
    /// (MCP <c>logging/setLevel</c>). Best-effort: throws only if the caller wants to surface the
    /// error; a server that doesn't support logging may reject it. Enables the client's log stream.
    /// </summary>
    Task SetLoggingLevelAsync(LoggingLevel level, CancellationToken cancellationToken);

    /// <summary>Lists the server's tools (name, description, input schema) over the open session.</summary>
    Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken cancellationToken);

    /// <summary>Lists the server's prompts (name, description, arguments) over the open session.</summary>
    Task<IReadOnlyList<PromptInfo>> ListPromptsAsync(CancellationToken cancellationToken);

    /// <summary>Calls a tool; <paramref name="progress"/> receives the server's progress notifications live.</summary>
    Task<ToolCallReport> CallToolAsync(string toolName, JsonObject arguments, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken);

    /// <summary>Reads a resource (or expands a URI template when <paramref name="arguments"/> is non-null).</summary>
    Task<ReadReport> ReadResourceAsync(string resourceOrTemplate, JsonObject? arguments, CancellationToken cancellationToken);

    /// <summary>Fetches a prompt's rendered messages.</summary>
    Task<PromptCallReport> GetPromptAsync(string promptName, JsonObject arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Argument completions for a prompt argument (MCP <c>completion/complete</c>, <c>ref/prompt</c>).
    /// Best-effort: returns an empty list when the server doesn't support completions.
    /// </summary>
    Task<IReadOnlyList<string>> CompletePromptArgumentAsync(string promptName, string argumentName, string partialValue, CancellationToken cancellationToken);

    /// <summary>
    /// Argument completions for a resource-template variable (MCP <c>completion/complete</c>,
    /// <c>ref/resource</c>). Best-effort: empty list when unsupported.
    /// </summary>
    Task<IReadOnlyList<string>> CompleteTemplateArgumentAsync(string uriTemplate, string argumentName, string partialValue, CancellationToken cancellationToken);
}
