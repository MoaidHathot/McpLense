using System.Text.Json.Nodes;
using McpLense.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace McpLense;

/// <summary>
/// Per-connection knobs threaded into the live MCP client: the <see cref="IServerInteraction"/> that
/// services server-initiated traffic, and whether to suppress the standalone GET event-stream
/// (suppressed by default for session safety; the runtime <c>--server-stream</c> opt-in flips it).
/// </summary>
internal sealed record McpConnectOptions(IServerInteraction Interaction, bool SuppressStandaloneStream = true)
{
    public static McpConnectOptions Default { get; } = new(LoggingServerInteraction.Instance);

    public McpConnectOptions With(IServerInteraction? interaction = null, bool? suppressStandaloneStream = null)
        => new(interaction ?? Interaction, suppressStandaloneStream ?? SuppressStandaloneStream);
}

/// <summary>
/// Receives the server-initiated half of the MCP protocol on a live client: server-&gt;client
/// requests (<c>sampling/createMessage</c>, <c>elicitation/create</c>, <c>roots/list</c>) and
/// notifications (logging messages, list-changed, ...). McpLense wires an implementation into every
/// live connection so these otherwise-invisible interactions are surfaced (and, in the TUI, shown to
/// and answered by the user). <see cref="Capabilities"/> is advertised during <c>initialize</c> and
/// gates what the server is allowed to send.
/// </summary>
internal interface IServerInteraction
{
    /// <summary>Client capabilities to advertise (set Sampling/Elicitation/Roots non-null to enable).</summary>
    ClientCapabilities Capabilities { get; }

    ValueTask<CreateMessageResult> CreateMessageAsync(CreateMessageRequestParams? request, IProgress<ProgressNotificationValue> progress, CancellationToken cancellationToken);

    ValueTask<ElicitResult> ElicitAsync(ElicitRequestParams? request, CancellationToken cancellationToken);

    ValueTask<ListRootsResult> ListRootsAsync(ListRootsRequestParams? request, CancellationToken cancellationToken);

    ValueTask OnNotificationAsync(string method, JsonNode? parameters, CancellationToken cancellationToken);
}

/// <summary>
/// Default, non-interactive <see cref="IServerInteraction"/>: it advertises the capabilities (so the
/// server WILL send these requests/notifications, making them observable) and surfaces every one via
/// <see cref="McpLenseLog"/>. It cannot interactively answer, so it declines elicitation, returns no
/// roots, and refuses sampling with a clear error - enough for one-shot <c>inspect</c>/<c>call</c> to
/// SEE what the server tried to do.
/// </summary>
internal sealed class LoggingServerInteraction : IServerInteraction
{
    public static LoggingServerInteraction Instance { get; } = new();

    public ClientCapabilities Capabilities { get; } = new()
    {
        Sampling = new SamplingCapability(),
        Elicitation = new ElicitationCapability(),
        Roots = new RootsCapability()
    };

    public ValueTask<CreateMessageResult> CreateMessageAsync(CreateMessageRequestParams? request, IProgress<ProgressNotificationValue> progress, CancellationToken cancellationToken)
    {
        McpLenseLog.Write($"server-request: sampling/createMessage ({request?.Messages?.Count ?? 0} message(s), maxTokens={request?.MaxTokens}) - refused (no model; use the TUI or wire a sampling backend to answer).");
        throw new InvalidOperationException("This MCP client cannot service sampling/createMessage (no model configured).");
    }

    public ValueTask<ElicitResult> ElicitAsync(ElicitRequestParams? request, CancellationToken cancellationToken)
    {
        McpLenseLog.Write($"server-request: elicitation/create - declined ({Quote(request?.Message)}).");
        return ValueTask.FromResult(new ElicitResult { Action = "decline" });
    }

    public ValueTask<ListRootsResult> ListRootsAsync(ListRootsRequestParams? request, CancellationToken cancellationToken)
    {
        McpLenseLog.Write("server-request: roots/list - returning no roots.");
        return ValueTask.FromResult(new ListRootsResult { Roots = [] });
    }

    public ValueTask OnNotificationAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        McpLenseLog.Write($"server-notification: {method}{SummarizeNotification(parameters)}");
        return ValueTask.CompletedTask;
    }

    private static string Quote(string? text)
        => string.IsNullOrWhiteSpace(text) ? "(no message)" : $"\"{text}\"";

    private static string SummarizeNotification(JsonNode? parameters)
    {
        if (parameters is null)
        {
            return string.Empty;
        }

        var text = parameters.ToJsonString();
        if (text.Length > 200)
        {
            text = text[..199] + "…";
        }

        return " " + text;
    }
}
