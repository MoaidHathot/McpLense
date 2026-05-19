using System.Text.Json;

namespace McpLense.Mcp;

/// <summary>
/// Unified per-command handler abstraction. Replaces the giant switch in
/// <see cref="McpExecutor"/> with a dictionary-driven dispatch so adding a new command is
/// register-a-handler rather than touch-a-switch.
/// </summary>
/// <remarks>
/// Each handler receives the parsed command + JSON options + cancellation. Handlers MUST
/// NOT call <see cref="TargetResolver.ResolveAsync"/> themselves when they need profile
/// attachment - the executor still runs that on commands that share the resolve-then-attach
/// path (inspect / tools / resources / call / read / prompt). The handler-specific
/// commands that own their own flow (scan / auth-scan / observe / fetch-resource / diff /
/// login / logout) get the raw <see cref="ParsedCommand"/> and do everything themselves.
/// </remarks>
internal interface ICommandHandler
{
    /// <summary>The command this handler implements.</summary>
    AppCommand Command { get; }

    /// <summary>True when the executor should pre-resolve targets + attach profiles before invoking.</summary>
    bool RequiresResolvedServers { get; }

    /// <summary>
    /// Execute the handler. <paramref name="servers"/> is the pre-resolved list when
    /// <see cref="RequiresResolvedServers"/> is true; otherwise null and the handler
    /// resolves on its own.
    /// </summary>
    Task<ExecutionOutcome> ExecuteAsync(
        ParsedCommand command,
        IReadOnlyList<ResolvedServer>? servers,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken);
}
