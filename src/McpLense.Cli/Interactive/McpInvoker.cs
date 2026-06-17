namespace McpLense;

/// <summary>Opens a live <see cref="IMcpSession"/> for the named server (scoping multi-server targets).</summary>
internal delegate Task<IMcpSession> McpSessionConnector(string serverName, CancellationToken cancellationToken);

/// <summary>A rendered invocation result: text to show plus an error flag for colouring.</summary>
internal sealed record InvokeResult(string Text, bool HasErrors);

/// <summary>
/// Supplies argument-completion suggestions for one prompt or one resource template, bound to a
/// specific argument name when queried. Abstracts <see cref="IMcpSession"/> so the elicitor can be
/// unit-tested with a fake.
/// </summary>
internal interface ICompletionSource
{
    Task<IReadOnlyList<string>> CompleteAsync(string argumentName, string partialValue, CancellationToken cancellationToken);
}

/// <summary>Completion source for a prompt's arguments (MCP <c>ref/prompt</c>).</summary>
internal sealed class PromptCompletionSource(IMcpSession session, string promptName) : ICompletionSource
{
    public Task<IReadOnlyList<string>> CompleteAsync(string argumentName, string partialValue, CancellationToken cancellationToken)
        => session.CompletePromptArgumentAsync(promptName, argumentName, partialValue, cancellationToken);
}

/// <summary>Completion source for a resource template's variables (MCP <c>ref/resource</c>).</summary>
internal sealed class TemplateCompletionSource(IMcpSession session, string uriTemplate) : ICompletionSource
{
    public Task<IReadOnlyList<string>> CompleteAsync(string argumentName, string partialValue, CancellationToken cancellationToken)
        => session.CompleteTemplateArgumentAsync(uriTemplate, argumentName, partialValue, cancellationToken);
}

/// <summary>Turns a session report into a rendered <see cref="InvokeResult"/> and builds connectors.</summary>
internal static class InvocationRenderer
{
    public static bool HasErrors(object report) => report switch
    {
        ToolCallReport tool => tool.Error is not null || tool.Result?.IsError == true,
        ReadReport read => read.Error is not null,
        PromptCallReport prompt => prompt.Error is not null,
        _ => false
    };

    public static InvokeResult Render(object report)
        => new(TextFormatter.Format(report, App.JsonOptions), HasErrors(report));

    /// <summary>
    /// Builds a connector that opens a session against the chosen server by re-using the command
    /// the TUI / interactive flow was launched with (scoped to that server, progress/interactive off).
    /// The optional <paramref name="interaction"/> services the server-initiated half of the protocol
    /// (sampling / elicitation / roots / notifications); when null the executor falls back to the
    /// default logging interaction.
    /// </summary>
    public static McpSessionConnector ConnectorFor(ParsedCommand command, IServerInteraction? interaction = null)
        => (serverName, cancellationToken) => McpExecutor.ConnectAsync(
            command with
            {
                ProgressEnabled = false,
                Interactive = false,
                Target = command.Target with { ServerNames = new[] { serverName } }
            },
            cancellationToken,
            interaction);
}
