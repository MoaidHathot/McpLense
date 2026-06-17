using System.Text.Json;
using McpLense.Diagnostics;
using McpLense.Mcp;
using McpLense.Scanning;
using McpLense.Scanning.TargetResolution;

namespace McpLense;

// Dictionary-driven command dispatch. Each AppCommand maps to an ICommandHandler that declares how
// much of the shared resolve -> overlay -> authenticate pipeline it needs (ServerResolution) and
// then runs its command-specific logic. The handlers are nested so they can call McpExecutor's
// existing private helpers (DispatchProfileLoginAsync, InspectAsync, ...) without widening their
// visibility; the bodies are the same logic the old ExecuteAsync switch inlined.
internal static partial class McpExecutor
{
    private static readonly IReadOnlyDictionary<AppCommand, ICommandHandler> Handlers =
        new ICommandHandler[]
        {
            new LoginHandler(),
            new LogoutHandler(),
            new DiffHandler(),
            new ScanHandler(),
            new AuthScanHandler(),
            new ObserveHandler(),
            new FetchResourceHandler(),
            new InspectHandler(),
            new ToolsHandler(),
            new ResourcesHandler(),
            new PromptsHandler(),
            new CallHandler(),
            new ReadHandler(),
            new PromptHandler()
        }.ToDictionary(handler => handler.Command);

    /// <summary>
    /// Shared prep for every command that opens a session: load the unified ScanConfig (needed
    /// BEFORE resolution so an <c>@name</c> reference resolves to a URL, and AFTER so the per-target
    /// overlay applies), resolve the target(s), and apply the overlay. Returns the (possibly
    /// named-reference-updated) command alongside the resolved servers.
    /// </summary>
    private static async Task<(ParsedCommand Command, IReadOnlyList<ResolvedServer> Servers)> ResolveAndOverlayAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var scanConfigPaths = TargetConfigLoading.ResolveScanConfigPaths(command.Target.ProfilePaths);
        var scanConfig = await ScanConfigLoader.LoadAsync(scanConfigPaths, cancellationToken).ConfigureAwait(false);
        command = command with { Target = TargetOverlayApplicator.ResolveNamedReference(command.Target, scanConfig) };

        var servers = await TargetResolver.ResolveAsync(command.Target, cancellationToken);
        servers = TargetOverlayApplicator.Apply(
            servers,
            scanConfig,
            command.Target,
            cliDisables: null,
            quiet: command.Quiet,
            verbose: command.Verbose);
        return (command, servers);
    }

    /// <summary>
    /// Attaches auth profiles to HTTP servers that don't already carry inline auth, unless
    /// <c>--no-auth</c> short-circuits it (in which case requests go out unauthenticated).
    /// </summary>
    private static async Task<IReadOnlyList<ResolvedServer>> AuthenticateAsync(
        IReadOnlyList<ResolvedServer> servers,
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.Target.AuthOverrides.NoAuth)
        {
            return await AttachProfilesAsync(servers, command.Target, cancellationToken, command.Quiet, command.Verbose).ConfigureAwait(false);
        }

        if (!command.Quiet)
        {
            McpLenseLog.Write("auth: --no-auth supplied; sending unauthenticated.");
        }

        return servers;
    }

    // --- Tier None: own the entire flow, never resolve a network target ----------------------

    private sealed class LoginHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Login;
        public ServerResolution Resolution => ServerResolution.None;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            var report = await DispatchProfileLoginAsync(command.Target, cancellationToken).ConfigureAwait(false);
            return new ExecutionOutcome(report, report.Servers.Any(entry => !entry.Success));
        }
    }

    private sealed class LogoutHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Logout;
        public ServerResolution Resolution => ServerResolution.None;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            var report = await DispatchProfileLogoutAsync(command.Target, cancellationToken).ConfigureAwait(false);
            return new ExecutionOutcome(report, report.Servers.Any(entry => !entry.Success));
        }
    }

    private sealed class DiffHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Diff;
        public ServerResolution Resolution => ServerResolution.None;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            // Pure file-to-file diff: no scan, just deserialize two baseline files and emit the
            // structural diff. The CLI passes the two paths via Subject + DiffBaselinePath.
            if (string.IsNullOrEmpty(command.Subject) || string.IsNullOrEmpty(command.DiffBaselinePath))
            {
                throw new UserInputException("'diff' requires two baseline paths: 'mcplense diff <before> <after>'.");
            }

            var before = await BaselineWriter.ReadAsync(command.Subject, cancellationToken).ConfigureAwait(false);
            var after = await BaselineWriter.ReadAsync(command.DiffBaselinePath, cancellationToken).ConfigureAwait(false);
            return new ExecutionOutcome(ScanDiff.Diff(before, after), false);
        }
    }

    private sealed class ScanHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Scan;
        public ServerResolution Resolution => ServerResolution.None;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            var cliEnables = command.CheckEnables is null ? null : new HashSet<string>(command.CheckEnables, StringComparer.OrdinalIgnoreCase);
            var cliDisables = command.CheckDisables is null ? null : new HashSet<string>(command.CheckDisables, StringComparer.OrdinalIgnoreCase);
            var parallel = command.ParallelServers ?? 1;

            // Progress reporting: silent in --quiet mode; basic [n/N] line otherwise.
            Action<int, int, string, TimeSpan>? progressCallback = null;
            if (!command.Quiet)
            {
                progressCallback = (index, total, name, elapsed) =>
                {
                    McpLenseLog.Write($"[{index}/{total}] {name}: ok ({elapsed.TotalSeconds:F1}s)");
                };
            }

            var scanReport = await ScanCommandDispatcher.RunAsync(
                command.Target,
                command.Timeout,
                cliEnables,
                cliDisables,
                cancellationToken,
                maxDegreeOfParallelism: parallel,
                progress: progressCallback,
                quiet: command.Quiet,
                verbose: command.Verbose,
                scanPluginPaths: command.ScanPlugins).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(command.BaselinePath))
            {
                var resolvedPath = BaselineWriter.ResolvePath(command.BaselinePath, null, scanReport);
                await BaselineWriter.WriteAsync(resolvedPath, scanReport, cancellationToken).ConfigureAwait(false);
                if (!command.Quiet)
                {
                    McpLenseLog.Write($"baseline written: {resolvedPath}");
                }
            }

            if (!string.IsNullOrEmpty(command.DiffBaselinePath))
            {
                var baseline = await BaselineWriter.ReadAsync(command.DiffBaselinePath, cancellationToken).ConfigureAwait(false);
                return new ExecutionOutcome(ScanDiff.Diff(baseline, scanReport), false);
            }

            return new ExecutionOutcome(scanReport, false);
        }
    }

    // --- Tier ResolveOnly: resolve + overlay, but no executor-driven auth attach -------------

    private sealed class AuthScanHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.AuthScan;
        public ServerResolution Resolution => ServerResolution.ResolveOnly;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            var report = await DispatchAuthScanAsync(servers!, command.Target, command.Timeout, cancellationToken).ConfigureAwait(false);
            return new ExecutionOutcome(report, report.Servers.Any(entry => entry.Error is not null));
        }
    }

    private sealed class ObserveHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Observe;
        // ResolveOnly so the @name reference + overlay are applied to command.Target; the observe
        // dispatcher re-resolves from command.Target and ignores the resolved server list.
        public ServerResolution Resolution => ServerResolution.ResolveOnly;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
            => await DispatchObserveAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private sealed class FetchResourceHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.FetchResource;
        // ResolveOnly: fetch-resource attaches profiles itself (unconditionally), so it is not on
        // the standard authenticate path.
        public ServerResolution Resolution => ServerResolution.ResolveOnly;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(command.Subject))
            {
                throw new UserInputException("'fetch-resource' requires a resource URI as the first positional argument.");
            }

            var withAuth = await AttachProfilesAsync(servers!, command.Target, cancellationToken, command.Quiet, command.Verbose).ConfigureAwait(false);
            return await ReadResourceAsync(SingleServer(withAuth), command.Subject, command.Arguments, command.Timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    // --- Tier ResolveAndAuthenticate: the standard list/invoke commands ----------------------

    private sealed class InspectHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Inspect;
        public ServerResolution Resolution => ServerResolution.ResolveAndAuthenticate;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
            => await InspectAsync(servers!, command.Timeout, cancellationToken);
    }

    private sealed class ToolsHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Tools;
        public ServerResolution Resolution => ServerResolution.ResolveAndAuthenticate;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
            => await ListToolsAsync(servers!, command.Timeout, cancellationToken);
    }

    private sealed class ResourcesHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Resources;
        public ServerResolution Resolution => ServerResolution.ResolveAndAuthenticate;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
            => await ListResourcesAsync(servers!, command.Timeout, cancellationToken);
    }

    private sealed class PromptsHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Prompts;
        public ServerResolution Resolution => ServerResolution.ResolveAndAuthenticate;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
            => await ListPromptsAsync(servers!, command.Timeout, cancellationToken);
    }

    private sealed class CallHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Call;
        public ServerResolution Resolution => ServerResolution.ResolveAndAuthenticate;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
            => await CallToolAsync(SingleServer(servers!), command.Subject!, command.Arguments!, command.Timeout, command.ProgressEnabled, cancellationToken);
    }

    private sealed class ReadHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Read;
        public ServerResolution Resolution => ServerResolution.ResolveAndAuthenticate;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
            => await ReadResourceAsync(SingleServer(servers!), command.Subject!, command.Arguments, command.Timeout, cancellationToken);
    }

    private sealed class PromptHandler : ICommandHandler
    {
        public AppCommand Command => AppCommand.Prompt;
        public ServerResolution Resolution => ServerResolution.ResolveAndAuthenticate;

        public async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, IReadOnlyList<ResolvedServer>? servers, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
            => await GetPromptAsync(SingleServer(servers!), command.Subject!, command.Arguments!, command.Timeout, cancellationToken);
    }
}
