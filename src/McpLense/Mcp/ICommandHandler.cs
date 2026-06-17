using System.Text.Json;

namespace McpLense.Mcp;

/// <summary>How much of the shared resolve -> overlay -> authenticate pipeline the executor runs
/// before invoking a command handler.</summary>
internal enum ServerResolution
{
    /// <summary>The command owns its entire flow; it never resolves a network target
    /// (login / logout / diff / scan). The handler receives a null server list.</summary>
    None,

    /// <summary>Resolve + overlay the target (so <c>@name</c> references and per-target headers are
    /// applied) but do NOT attach auth profiles. For commands that either don't open a session
    /// (auth-scan classifies, observe re-resolves) or attach auth themselves (fetch-resource).</summary>
    ResolveOnly,

    /// <summary>Resolve + overlay AND attach auth profiles (honouring <c>--no-auth</c>). The standard
    /// path for inspect / tools / resources / prompts / call / read / prompt.</summary>
    ResolveAndAuthenticate
}

/// <summary>
/// Unified per-command handler abstraction. Replaces the giant if-chain + switch in
/// <see cref="McpExecutor"/> with a dictionary-driven dispatch so adding a new command is
/// register-a-handler rather than touch-a-switch. Each handler declares how much of the shared
/// pipeline it needs via <see cref="Resolution"/>; the executor runs exactly that much and then
/// invokes <see cref="ExecuteAsync"/> with the resulting (possibly null) server list.
/// </summary>
internal interface ICommandHandler
{
    /// <summary>The command this handler implements.</summary>
    AppCommand Command { get; }

    /// <summary>How much of the shared resolve/overlay/auth pipeline the executor runs first.</summary>
    ServerResolution Resolution { get; }

    /// <summary>
    /// Execute the handler. <paramref name="servers"/> is the pre-resolved (and, for
    /// <see cref="ServerResolution.ResolveAndAuthenticate"/>, auth-attached) list when
    /// <see cref="Resolution"/> is not <see cref="ServerResolution.None"/>; otherwise null.
    /// </summary>
    Task<ExecutionOutcome> ExecuteAsync(
        ParsedCommand command,
        IReadOnlyList<ResolvedServer>? servers,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken);
}
