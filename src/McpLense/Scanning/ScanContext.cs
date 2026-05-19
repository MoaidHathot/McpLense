using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace McpLense.Scanning;

/// <summary>
/// Per-server, per-scan scratchpad shared with every <see cref="IScanCheck"/>. Holds the
/// inputs (target, config, services) and the lazy resources every check might want without
/// duplicating work (auth scan result, MCP session, transport probe). Checks read prior
/// checks' outputs via <see cref="GetPriorOutput(string)"/>.
/// </summary>
/// <remarks>
/// Lifecycle: one <see cref="ScanContext"/> per server. The pipeline disposes it after every
/// enabled check has run; lazy resources (the MCP session in particular) are disposed at
/// that point.
/// </remarks>
public sealed class ScanContext : IAsyncDisposable
{
    private readonly Dictionary<string, JsonNode?> _priorOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sessionLock = new();
    private Task<McpClient?>? _sessionTask;
    private McpClient? _session;
    private string? _sessionFetchedVia;
    private string? _sessionError;

    // The auth check publishes its chosen auth path here (or null when anonymous). The
    // pipeline's session factory reads these to know which credentials to use when opening
    // a session lazily on behalf of a downstream check. Decoupling the AuthCheck output
    // from the session factory means AuthCheck doesn't have to also open the inspection
    // session - separation of concerns matches the per-check architecture.
    private ResolvedAuth? _activeAuth;
    private string _activeFetchedVia = "anonymous";

    internal ScanContext(
        ResolvedServer server,
        ScanConfig config,
        IServiceProvider services,
        TimeSpan handshakeTimeout,
        Func<ScanContext, CancellationToken, Task<(McpClient? Client, string? FetchedVia, string? Error)>> sessionFactory,
        IReadOnlyList<AuthProfile>? profiles = null,
        AuthOverrides? authOverrides = null)
    {
        Server = server;
        Config = config;
        Services = services;
        HandshakeTimeout = handshakeTimeout;
        SessionFactory = sessionFactory;
        Profiles = profiles ?? Array.Empty<AuthProfile>();
        AuthOverrides = authOverrides ?? AuthOverrides.Empty;
    }

    /// <summary>The target being scanned.</summary>
    public ResolvedServer Server { get; }

    /// <summary>Parsed scan configuration (per-check toggles, knobs).</summary>
    public ScanConfig Config { get; }

    /// <summary>
    /// All authentication profiles available to the scan. Checks that need to attach a
    /// specific profile read this directly rather than resolving via DI. Empty when no
    /// profile file was loaded.
    /// </summary>
    public IReadOnlyList<AuthProfile> Profiles { get; }

    /// <summary>
    /// CLI / host-supplied auth overrides (e.g. <c>--no-auth</c>, <c>--classify-only</c>,
    /// <c>--profile &lt;name&gt;</c>). Checks honour these without consulting DI.
    /// </summary>
    public AuthOverrides AuthOverrides { get; }

    /// <summary>DI provider for resolving HttpClient, ILogger, custom services, etc.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Per-handshake / per-session timeout in seconds.</summary>
    public TimeSpan HandshakeTimeout { get; }

    internal Func<ScanContext, CancellationToken, Task<(McpClient? Client, string? FetchedVia, string? Error)>> SessionFactory { get; }

    /// <summary>
    /// Outputs from checks that have already run (keyed by check id). Checks declared in
    /// <see cref="IScanCheck.DependsOn"/> are guaranteed to be present when the dependent
    /// check executes.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?> PriorOutputs => _priorOutputs;

    /// <summary>Convenience accessor.</summary>
    public JsonNode? GetPriorOutput(string checkId)
        => _priorOutputs.TryGetValue(checkId, out var value) ? value : null;

    internal void RecordOutput(string checkId, JsonNode? data) => _priorOutputs[checkId] = data;

    /// <summary>
    /// Called by <see cref="AuthCheck"/> after it picks the best available auth path.
    /// Subsequent session-opening checks use this without having to re-decide.
    /// </summary>
    internal void PublishActiveAuth(ResolvedAuth? auth, string fetchedVia)
    {
        _activeAuth = auth;
        _activeFetchedVia = fetchedVia;
    }

    internal ResolvedAuth? ActiveAuth => _activeAuth;
    internal string ActiveFetchedVia => _activeFetchedVia;

    /// <summary>
    /// Returns the existing MCP session if any check already opened one (and it didn't fail),
    /// otherwise opens one via the pipeline-supplied session factory. The factory picks the
    /// best available auth path (anonymous when the auth check classified the server as
    /// anonymous; the first successful profile otherwise). Returned client is owned by this
    /// context - do NOT dispose. Returns <c>null</c> when no auth path worked.
    /// </summary>
    public Task<McpClient?> GetSessionAsync(CancellationToken cancellationToken)
    {
        lock (_sessionLock)
        {
            _sessionTask ??= OpenSessionAsync(cancellationToken);
            return _sessionTask;
        }
    }

    /// <summary>
    /// How the active session was authenticated, e.g. <c>"anonymous"</c> or
    /// <c>"profile:agent365"</c>. Null until <see cref="GetSessionAsync"/> has been called
    /// (and even then, null when no auth path worked).
    /// </summary>
    public string? SessionFetchedVia => _sessionFetchedVia;

    /// <summary>Error from the session-open attempt when <see cref="GetSessionAsync"/> returned null.</summary>
    public string? SessionError => _sessionError;

    private async Task<McpClient?> OpenSessionAsync(CancellationToken cancellationToken)
    {
        var (client, fetchedVia, error) = await SessionFactory(this, cancellationToken).ConfigureAwait(false);
        _session = client;
        _sessionFetchedVia = fetchedVia;
        _sessionError = error;
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }

    /// <summary>For test fixtures: build a context with pre-loaded prior outputs.</summary>
    internal static ScanContext ForTesting(
        ResolvedServer server,
        ScanConfig config,
        IServiceProvider services,
        Func<ScanContext, CancellationToken, Task<(McpClient? Client, string? FetchedVia, string? Error)>>? sessionFactory = null,
        TimeSpan? handshakeTimeout = null,
        IReadOnlyDictionary<string, JsonNode?>? priorOutputs = null,
        IReadOnlyList<AuthProfile>? profiles = null,
        AuthOverrides? authOverrides = null)
    {
        var ctx = new ScanContext(
            server,
            config,
            services,
            handshakeTimeout ?? TimeSpan.FromSeconds(30),
            sessionFactory ?? ((_, _) => Task.FromResult<(McpClient?, string?, string?)>((null, null, "test: no session factory"))),
            profiles,
            authOverrides);

        if (priorOutputs is not null)
        {
            foreach (var (k, v) in priorOutputs)
            {
                ctx._priorOutputs[k] = v;
            }
        }

        return ctx;
    }
}
