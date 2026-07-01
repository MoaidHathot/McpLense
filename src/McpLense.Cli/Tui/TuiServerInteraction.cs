using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace McpLense;

/// <summary>One captured server-initiated interaction, ready to show the user after an invocation.</summary>
internal sealed record ServerInitiatedEvent(string Method, string Detail, string? Response);

/// <summary>
/// TUI <see cref="IServerInteraction"/>: advertises sampling / elicitation / roots so the server
/// actually exercises them, answers with safe defaults (refuse sampling, decline elicitation, no
/// roots) and CAPTURES every server-initiated request/notification so the TUI can show the user what
/// the server tried to do once the invocation finishes. Answering with defaults instead of prompting
/// mid-call keeps the Spectre progress bar from being corrupted by a nested prompt, and keeps the
/// capture lock-free (handlers run on SDK threads while a call is in flight).
///
/// <para>
/// Log notifications (<c>notifications/message</c>) are additionally parsed into structured
/// <see cref="TuiLogEntry"/> records and appended to a persistent, session-lifetime log buffer so
/// the TUI can render a live tail and a full scrollback of every log received since connecting.
/// </para>
/// </summary>
internal sealed class TuiServerInteraction : IServerInteraction
{
    private readonly ConcurrentQueue<ServerInitiatedEvent> _captured = new();

    // Append-only, session-lifetime log buffer. A lock (rather than a concurrent collection) keeps
    // snapshot reads consistent while SDK threads append during a call.
    private readonly object _logLock = new();
    private readonly List<TuiLogEntry> _logs = new();

    public ClientCapabilities Capabilities { get; } = new()
    {
        Sampling = new SamplingCapability(),
        Elicitation = new ElicitationCapability(),
        Roots = new RootsCapability()
    };

    public ValueTask<CreateMessageResult> CreateMessageAsync(CreateMessageRequestParams? request, IProgress<ProgressNotificationValue> progress, CancellationToken cancellationToken)
    {
        _captured.Enqueue(new ServerInitiatedEvent(
            "sampling/createMessage",
            $"{request?.Messages?.Count ?? 0} message(s), maxTokens={request?.MaxTokens}",
            "refused (no model wired into the explorer)"));
        throw new InvalidOperationException("This MCP client cannot service sampling/createMessage (no model configured).");
    }

    public ValueTask<ElicitResult> ElicitAsync(ElicitRequestParams? request, CancellationToken cancellationToken)
    {
        _captured.Enqueue(new ServerInitiatedEvent(
            "elicitation/create",
            string.IsNullOrWhiteSpace(request?.Message) ? "(no message)" : request!.Message!,
            "declined"));
        return ValueTask.FromResult(new ElicitResult { Action = "decline" });
    }

    public ValueTask<ListRootsResult> ListRootsAsync(ListRootsRequestParams? request, CancellationToken cancellationToken)
    {
        _captured.Enqueue(new ServerInitiatedEvent("roots/list", "(no arguments)", "returned no roots"));
        return ValueTask.FromResult(new ListRootsResult { Roots = [] });
    }

    public ValueTask OnNotificationAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        if (method == NotificationMethods.LoggingMessageNotification)
        {
            // A log message: keep it in the persistent buffer (for the tail + the Logs viewer)
            // rather than the transient post-invocation table.
            var entry = TuiLogEntry.FromNotification(parameters);
            lock (_logLock)
            {
                _logs.Add(entry);
            }
            return ValueTask.CompletedTask;
        }

        _captured.Enqueue(new ServerInitiatedEvent(method, Summarize(parameters), Response: null));
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes and returns everything captured since the last drain (oldest first).</summary>
    public IReadOnlyList<ServerInitiatedEvent> Drain()
    {
        var drained = new List<ServerInitiatedEvent>();
        while (_captured.TryDequeue(out var item))
        {
            drained.Add(item);
        }

        return drained;
    }

    /// <summary>Total number of log entries received since connecting (for badges / "N new" hints).</summary>
    public int LogCount
    {
        get { lock (_logLock) { return _logs.Count; } }
    }

    /// <summary>
    /// A point-in-time copy of every log entry received since connecting (oldest first). Optionally
    /// filtered to entries at or above <paramref name="minLevel"/> (by severity, Emergency highest).
    /// </summary>
    public IReadOnlyList<TuiLogEntry> LogSnapshot(LoggingLevel? minLevel = null)
    {
        lock (_logLock)
        {
            if (minLevel is null)
            {
                return _logs.ToArray();
            }

            var threshold = (int)minLevel.Value;
            return _logs.Where(e => (int)e.Level >= threshold).ToArray();
        }
    }

    private static string Summarize(JsonNode? parameters)
    {
        if (parameters is null)
        {
            return "(no params)";
        }

        var text = parameters.ToJsonString();
        return text.Length <= 160 ? text : text[..159] + "\u2026";
    }
}

