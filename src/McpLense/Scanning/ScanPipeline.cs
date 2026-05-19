using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace McpLense.Scanning;

/// <summary>
/// Orchestrates the registered <see cref="IScanCheck"/>s for one or more servers. Builds a
/// <see cref="ScanContext"/> per server, resolves which checks are enabled, runs them in
/// topological order (parallel within a server where independent), captures per-check
/// timings, and assembles the final <see cref="ScanReport"/>. Errors thrown by checks are
/// caught and surfaced as <see cref="CheckOutcome.Error"/> so a single misbehaving check
/// can't poison the rest of the report.
/// </summary>
public sealed class ScanPipeline
{
    private readonly IReadOnlyList<IScanCheck> _checks;
    private readonly ScanConfig _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<ScanPipeline> _logger;
    private readonly IReadOnlySet<string>? _cliEnables;
    private readonly IReadOnlySet<string>? _cliDisables;

    internal ScanPipeline(
        IReadOnlyList<IScanCheck> checks,
        ScanConfig config,
        IServiceProvider services,
        ILogger<ScanPipeline>? logger = null,
        IReadOnlySet<string>? cliEnables = null,
        IReadOnlySet<string>? cliDisables = null)
    {
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? NullLogger<ScanPipeline>.Instance;
        _cliEnables = cliEnables;
        _cliDisables = cliDisables;

        WarnAboutUnknownConfigIds();
    }

    /// <summary>
    /// Runs the pipeline against every supplied target. Servers are processed with up to
    /// <paramref name="maxDegreeOfParallelism"/> concurrent workers (default 1 = sequential).
    /// Every enabled check within a single server still runs in parallel respecting
    /// <see cref="IScanCheck.DependsOn"/>.
    /// </summary>
    /// <param name="targets">Resolved targets to scan.</param>
    /// <param name="handshakeTimeout">Per-server MCP handshake timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="maxDegreeOfParallelism">
    /// How many servers to scan concurrently. Default <c>1</c>. Useful for fleet scans where
    /// per-server I/O dominates wall-clock; setting too high will saturate the outbound
    /// socket pool.
    /// </param>
    /// <param name="progress">
    /// Optional callback invoked once per server when the scan completes. The arguments are
    /// (1-based index, total, server-name, elapsed). Used by the CLI's progress renderer;
    /// nullable so library consumers don't have to wire one up.
    /// </param>
    public async Task<ScanReport> RunAsync(
        IReadOnlyList<ResolvedServer> targets,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken,
        int maxDegreeOfParallelism = 1,
        Action<int, int, string, TimeSpan>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (maxDegreeOfParallelism < 1)
        {
            maxDegreeOfParallelism = 1;
        }

        // Sequential path: keeps determinism / log ordering for the default case.
        if (maxDegreeOfParallelism == 1 || targets.Count <= 1)
        {
            var serverResults = new List<ServerScanResult>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await RunOneAsync(targets[i], handshakeTimeout, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                serverResults.Add(result);
                progress?.Invoke(i + 1, targets.Count, targets[i].Name, sw.Elapsed);
            }

            return new ScanReport(
                GeneratedAt: DateTimeOffset.UtcNow,
                SchemaVersion: "1",
                Servers: serverResults);
        }

        // Parallel path: bounded concurrency via SemaphoreSlim. Each server completes its
        // own pipeline independently; the report's `servers` array stays in input order so
        // the diff engine's stable identity holds.
        var slots = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);
        var resultsByIndex = new ServerScanResult?[targets.Count];
        var completed = 0;

        async Task RunIndexAsync(int index)
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await RunOneAsync(targets[index], handshakeTimeout, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                resultsByIndex[index] = result;
                var done = Interlocked.Increment(ref completed);
                progress?.Invoke(done, targets.Count, targets[index].Name, sw.Elapsed);
            }
            finally
            {
                slots.Release();
            }
        }

        var tasks = new Task[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            tasks[i] = RunIndexAsync(i);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new ScanReport(
            GeneratedAt: DateTimeOffset.UtcNow,
            SchemaVersion: "1",
            Servers: resultsByIndex.Where(r => r is not null).Select(r => r!).ToArray());
    }

    private async Task<ServerScanResult> RunOneAsync(
        ResolvedServer server,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        // The session factory is invoked the FIRST time any check calls
        // ScanContext.GetSessionAsync. It's set up at context-build time so checks don't
        // have to know about the pipeline's internals.
        var sessionFactory = BuildSessionFactory();

        // Surface profiles + auth overrides on the context directly. AuthCheck used to fish
        // them out of DI; first-class properties make the API self-documenting and decouple
        // checks from DI registrations.
        var profiles = _services.GetService(typeof(IReadOnlyList<AuthProfile>)) as IReadOnlyList<AuthProfile>;
        var overrides = _services.GetService(typeof(AuthOverrides)) as AuthOverrides;

        // Per-server timeout override (from `targets[].timeoutSeconds`) wins over the global
        // CLI timeout when set.
        var effectiveTimeout = server.HandshakeTimeout ?? handshakeTimeout;

        await using var context = new ScanContext(
            server,
            _config,
            _services,
            effectiveTimeout,
            sessionFactory,
            profiles,
            overrides);

        // Union the global CLI disables with the per-server overlay disables. This is the
        // ONLY place per-server `disabledChecks` propagation lives - keep it local so the
        // pipeline's public surface stays simple.
        IReadOnlySet<string>? effectiveDisables = _cliDisables;
        if (server.DisabledChecks is { Count: > 0 })
        {
            var merged = new HashSet<string>(_cliDisables ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            foreach (var id in server.DisabledChecks)
            {
                merged.Add(id);
            }
            effectiveDisables = merged;
        }

        var enabled = _checks.Where(c => _config.IsCheckEnabled(c, _cliEnables, effectiveDisables)).ToList();
        var idLookup = enabled.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);
        var ordered = TopoSort(enabled);

        var checks = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var timings = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string? topLevelError = null;

        // Within a single server we run independent checks in parallel: group by
        // dependency tier (each tier contains checks whose deps are all in earlier tiers).
        // This is the standard "topo sort -> layers" pattern.
        var tiers = BuildTiers(ordered, idLookup);
        foreach (var tier in tiers)
        {
            var tasks = tier.Select(check => RunCheckAsync(check, context, cancellationToken)).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            for (var i = 0; i < tier.Count; i++)
            {
                var check = tier[i];
                var (outcome, elapsedMs) = results[i];

                if (outcome.Ran)
                {
                    checks[check.Id] = outcome.Data;
                    context.RecordOutput(check.Id, outcome.Data);
                }

                if (outcome.Error is not null)
                {
                    checks[check.Id] = checks.TryGetValue(check.Id, out var existing) && existing is not null
                        ? AddErrorField(existing, outcome.Error)
                        : new JsonObject { ["error"] = outcome.Error };
                }

                timings[check.Id] = elapsedMs;
            }
        }

        return new ServerScanResult(
            Name: server.Name,
            Transport: server.Kind == ConnectionKind.Stdio ? "stdio" : "http",
            Target: server.Target,
            Checks: checks,
            Timings: timings,
            Error: topLevelError);
    }

    private async Task<(CheckOutcome Outcome, double ElapsedMs)> RunCheckAsync(
        IScanCheck check,
        ScanContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var outcome = await check.RunAsync(context, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return (outcome, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Real, user-driven cancellation: propagate so the whole pipeline can unwind.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // A different (linked / internal) cancellation source fired - e.g. a per-request
            // HttpClient timeout, TransportProbe's 15s cap, or the SDK's initialise timeout
            // wired through ConnectionTimeout. These come through as
            // OperationCanceledException whose token is NOT our pipeline token; if we
            // rethrew here the whole report would die because of one slow probe. Capture
            // as a per-check timeout so the rest of the scan still produces output.
            sw.Stop();
            _logger.LogWarning("Check {Id} timed out: {Message}", check.Id, ex.Message);
            return (new CheckOutcome(Ran: true, Data: null, Error: "Timed out."),
                    sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Check {Id} threw an unexpected exception", check.Id);
            return (new CheckOutcome(Ran: true, Data: null, Error: $"{ex.GetType().Name}: {ex.Message}"),
                    sw.Elapsed.TotalMilliseconds);
        }
    }

    private void WarnAboutUnknownConfigIds()
    {
        var known = _checks.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredId in _config.ConfiguredCheckIds)
        {
            if (!known.Contains(configuredId))
            {
                _logger.LogWarning(
                    "Config file references unknown check id '{Id}'; ignored. Loaded checks: {Known}",
                    configuredId,
                    string.Join(", ", known));
                Console.Error.WriteLine($"warning: scan.checks.{configuredId} in config file does not match any registered check id; ignored.");
            }
        }
    }

    /// <summary>
    /// Builds the per-server session factory: when a check first asks for an MCP session,
    /// the factory uses whatever auth path the AuthCheck published via
    /// <see cref="ScanContext.PublishActiveAuth"/>. Subsequent checks share that single
    /// session.
    /// </summary>
    private Func<ScanContext, CancellationToken, Task<(McpClient? Client, string? FetchedVia, string? Error)>> BuildSessionFactory()
    {
        return async (context, ct) =>
        {
            if (context.Server.Kind != ConnectionKind.Http || context.Server.Url is null)
            {
                return (null, null, "Target is not an HTTP MCP; cannot open MCP session.");
            }

            var serverForSession = context.Server with { Auth = context.ActiveAuth };
            var fetchedVia = context.ActiveFetchedVia;

            try
            {
                var client = await OpenSessionForAsync(serverForSession, context.HandshakeTimeout, ct).ConfigureAwait(false);
                return (client, fetchedVia, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, fetchedVia, $"{ex.GetType().Name}: {ex.Message}");
            }
        };
    }

    /// <summary>
    /// Static-shaped session opener that mirrors McpExecutor.CreateClientAsync's HTTP path.
    /// Kept here so the pipeline owns its own connection lifetime independent of the older
    /// executor; the existing executor will be deprecated once every command moves over.
    /// </summary>
    private static async Task<McpClient> OpenSessionForAsync(
        ResolvedServer server,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = server.Url!,
            Name = server.Name,
            TransportMode = server.Transport switch
            {
                TransportPreference.StreamableHttp => HttpTransportMode.StreamableHttp,
                TransportPreference.Sse => HttpTransportMode.Sse,
                _ => HttpTransportMode.AutoDetect
            },
            ConnectionTimeout = timeout
        };

        if (server.Headers.Count > 0)
        {
            // Avoid taking a hard dependency on the SDK's exact property name by setting
            // headers through reflection (same pattern used elsewhere). The SDK has churned
            // on this name historically.
            var prop = options.GetType().GetProperty("AdditionalHeaders");
            prop?.SetValue(options, server.Headers.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        }

        if (server.Auth is { Kind: not AuthKind.None })
        {
            var authHandler = AuthHandlerFactory.Create(server.Auth, server.Url);
            if (authHandler is not null)
            {
                authHandler.InnerHandler = new SocketsHttpHandler();
                var http = new HttpClient(authHandler, disposeHandler: true);
                return await McpClient.CreateAsync(new HttpClientTransport(options, http, ownsHttpClient: true), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static JsonNode AddErrorField(JsonNode existing, string error)
    {
        if (existing is JsonObject obj)
        {
            obj["error"] = error;
            return obj;
        }

        return new JsonObject { ["value"] = existing.DeepClone(), ["error"] = error };
    }

    /// <summary>Topo-sort by dependency edges; missing deps short-circuit (the check just sees no prior output for them).</summary>
    private static IReadOnlyList<IScanCheck> TopoSort(IReadOnlyList<IScanCheck> checks)
    {
        var sorted = new List<IScanCheck>(checks.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byId = checks.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);

        void Visit(IScanCheck check)
        {
            if (visited.Contains(check.Id))
            {
                return;
            }

            if (!seen.Add(check.Id))
            {
                throw new InvalidOperationException($"Cyclic dependency detected involving check '{check.Id}'.");
            }

            foreach (var depId in check.DependsOn)
            {
                if (byId.TryGetValue(depId, out var dep))
                {
                    Visit(dep);
                }
            }

            visited.Add(check.Id);
            sorted.Add(check);
        }

        foreach (var check in checks)
        {
            Visit(check);
        }

        return sorted;
    }

    private static IReadOnlyList<IReadOnlyList<IScanCheck>> BuildTiers(
        IReadOnlyList<IScanCheck> sorted,
        IReadOnlyDictionary<string, IScanCheck> idLookup)
    {
        // tier[id] = max(tier[deps]) + 1; checks with no dep are tier 0.
        var tierByCheck = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in sorted)
        {
            var maxDep = -1;
            foreach (var depId in check.DependsOn)
            {
                if (tierByCheck.TryGetValue(depId, out var depTier))
                {
                    maxDep = Math.Max(maxDep, depTier);
                }
            }

            tierByCheck[check.Id] = maxDep + 1;
        }

        var groups = sorted.GroupBy(c => tierByCheck[c.Id])
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<IScanCheck>)g.ToArray())
            .ToArray();

        return groups;
    }
}
