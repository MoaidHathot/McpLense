using System.Text.Json.Nodes;

namespace McpLense;

/// <summary>Outcome of a TUI-driven invocation: the rendered text plus an error flag for colouring.</summary>
internal sealed record InvokeResult(string Text, bool HasErrors);

/// <summary>
/// Executes a single tool call / resource read / prompt fetch against one already-resolved
/// server. Abstracted so the TUI can be unit-tested with a fake that never opens a transport.
/// </summary>
internal interface IMcpInvoker
{
    Task<InvokeResult> CallToolAsync(string serverName, string toolName, JsonObject arguments, CancellationToken cancellationToken);

    Task<InvokeResult> ReadResourceAsync(string serverName, string resourceOrTemplate, JsonObject? arguments, CancellationToken cancellationToken);

    Task<InvokeResult> GetPromptAsync(string serverName, string promptName, JsonObject arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IMcpInvoker"/>: re-drives <see cref="McpExecutor.ExecuteAsync"/> with the
/// same <see cref="ParsedCommand"/> the TUI was launched with, swapping in the chosen command +
/// subject + arguments. The whole target-resolution / auth-attachment pipeline is reused, so an
/// invocation authenticates exactly the way the equivalent <c>mcplense call/read/prompt</c> would.
/// </summary>
/// <remarks>
/// When the inspect step resolved more than one server (a multi-server <c>--config</c>), the
/// target is narrowed to the selected server via <see cref="TargetOptions.ServerNames"/> so the
/// executor's single-server requirement is satisfied. For single URL / stdio targets the target
/// is passed through untouched.
/// </remarks>
internal sealed class McpExecutorInvoker : IMcpInvoker
{
    private readonly ParsedCommand _command;
    private readonly bool _multiServer;

    public McpExecutorInvoker(ParsedCommand command, bool multiServer)
    {
        _command = command;
        _multiServer = multiServer;
    }

    public Task<InvokeResult> CallToolAsync(string serverName, string toolName, JsonObject arguments, CancellationToken cancellationToken)
        => RunAsync(serverName, AppCommand.Call, toolName, arguments, cancellationToken);

    public Task<InvokeResult> ReadResourceAsync(string serverName, string resourceOrTemplate, JsonObject? arguments, CancellationToken cancellationToken)
        => RunAsync(serverName, AppCommand.Read, resourceOrTemplate, arguments is { Count: > 0 } ? arguments : null, cancellationToken);

    public Task<InvokeResult> GetPromptAsync(string serverName, string promptName, JsonObject arguments, CancellationToken cancellationToken)
        => RunAsync(serverName, AppCommand.Prompt, promptName, arguments, cancellationToken);

    private async Task<InvokeResult> RunAsync(string serverName, AppCommand verb, string subject, JsonObject? arguments, CancellationToken cancellationToken)
    {
        var command = _command with
        {
            Command = verb,
            Subject = subject,
            Arguments = arguments,
            ProgressEnabled = false,
            Interactive = false,
            Format = OutputFormat.Text,
            Target = _multiServer ? _command.Target with { ServerNames = new[] { serverName } } : _command.Target
        };

        var outcome = await McpExecutor.ExecuteAsync(command, App.JsonOptions, cancellationToken).ConfigureAwait(false);
        return new InvokeResult(TextFormatter.Format(outcome.Payload, App.JsonOptions), outcome.HasErrors);
    }
}
