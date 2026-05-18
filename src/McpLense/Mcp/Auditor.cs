namespace McpLense;

/// <summary>
/// Discovery-only command driver that produces a full <see cref="AuditReport"/>: auth
/// classification (delegated to <see cref="AuthScanner"/>) plus every other surface the user
/// asked for (server identity, protocol details, advertised capabilities, full tool/prompt/
/// resource enumeration when reachable, TLS posture, security-relevant response headers,
/// OAuth authorization-server discovery, behaviour probes, stdio configuration).
/// </summary>
/// <remarks>
/// <para>
/// The audit is intentionally fact-only: every output field is a raw observation. We do not
/// label findings as "high risk" / "safe" / etc. The user explicitly asked for this so they
/// can apply policy downstream; consumers (humans or tooling) interpret the data.
/// </para>
/// <para>
/// Composition pattern: this class is a thin orchestrator. The work of each section is owned
/// by a single-responsibility component (<see cref="AuthScanner"/>, <see cref="ITransportProbe"/>,
/// <see cref="IAuthorizationServerProbe"/>, <see cref="IMcpSessionInspector"/>). Each is
/// independently testable; <see cref="Auditor"/>'s unit tests stub each one out and assert
/// that the right thing was called with the right inputs.
/// </para>
/// </remarks>
internal sealed class Auditor
{
    private readonly AuthScanner _authScanner;
    private readonly ITransportProbe _transportProbe;
    private readonly IAuthorizationServerProbe _authServerProbe;
    private readonly IMcpSessionInspector _sessionInspector;

    public Auditor(
        AuthScanner authScanner,
        ITransportProbe transportProbe,
        IAuthorizationServerProbe authServerProbe,
        IMcpSessionInspector sessionInspector)
    {
        _authScanner = authScanner ?? throw new ArgumentNullException(nameof(authScanner));
        _transportProbe = transportProbe ?? throw new ArgumentNullException(nameof(transportProbe));
        _authServerProbe = authServerProbe ?? throw new ArgumentNullException(nameof(authServerProbe));
        _sessionInspector = sessionInspector ?? throw new ArgumentNullException(nameof(sessionInspector));
    }

    /// <summary>Production entry-point. Owns its own probe + scanner stack.</summary>
    public static async Task<AuditReport> AuditAsync(
        IReadOnlyList<ResolvedServer> servers,
        IReadOnlyList<AuthProfile> profiles,
        AuthOverrides authOverrides,
        bool checkAuthorizationServers,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        using var authProbe = new AuthProbe();
        using var transportProbe = new TransportProbe();
        using var authServerProbe = new AuthorizationServerProbe();

        var authScanner = new AuthScanner(authProbe, new McpHandshakeProbe());
        var auditor = new Auditor(authScanner, transportProbe, authServerProbe, new McpSessionInspector());

        return await auditor.AuditCoreAsync(
            servers,
            profiles,
            authOverrides,
            checkAuthorizationServers,
            handshakeTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuditReport> AuditCoreAsync(
        IReadOnlyList<ResolvedServer> servers,
        IReadOnlyList<AuthProfile> profiles,
        AuthOverrides authOverrides,
        bool checkAuthorizationServers,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(authOverrides);

        // The auth scan runs first because its output (classification + which profile worked)
        // tells the rest of the audit how to reach the server. We always run it - even when
        // the user passes --no-auth - because the classification is a fact the audit reports.
        var authReport = await _authScanner.ScanCoreAsync(servers, profiles, authOverrides, handshakeTimeout, cancellationToken).ConfigureAwait(false);

        var serverEntries = new List<ServerAudit>(servers.Count);
        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];
            var auth = authReport.Servers[index];
            serverEntries.Add(await AuditOneAsync(
                server,
                auth,
                profiles,
                authOverrides,
                checkAuthorizationServers,
                handshakeTimeout,
                cancellationToken).ConfigureAwait(false));
        }

        return new AuditReport(DateTimeOffset.UtcNow, serverEntries);
    }

    private async Task<ServerAudit> AuditOneAsync(
        ResolvedServer server,
        ServerAuthScan auth,
        IReadOnlyList<AuthProfile> profiles,
        AuthOverrides authOverrides,
        bool checkAuthorizationServers,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        // Stdio targets: surface the resolved command line + env + cwd + the auth scan (which
        // already marked them stdio). No TLS, no headers, no MCP session - all out of scope
        // for v1's stdio handling per user direction.
        if (server.Kind != ConnectionKind.Http || server.Url is null)
        {
            return new ServerAudit(
                Name: server.Name,
                Transport: "stdio",
                Target: server.Target,
                Auth: auth,
                ServerInfo: null,
                Protocol: null,
                Tools: EmptyToolListing("stdio target: HTTP-based tool enumeration does not apply."),
                Prompts: EmptyPromptListing("stdio target: HTTP-based prompt enumeration does not apply."),
                Resources: EmptyResourceListing("stdio target: HTTP-based resource enumeration does not apply."),
                Security: new SecuritySummary(MixedContent: false, Tls: null, ResponseHeaders: null),
                OAuth: null,
                Behavior: new BehaviorProbes(CallNonExistentTool: null),
                Stdio: new StdioSummary(
                    Command: server.Command ?? string.Empty,
                    Arguments: server.CommandArguments.ToArray(),
                    WorkingDirectory: server.WorkingDirectory,
                    Environment: new Dictionary<string, string>(server.Environment, StringComparer.OrdinalIgnoreCase)));
        }

        // HTTP target. Run the section probes in parallel where it's safe; the session
        // inspector and transport probe both hit the server but are independent (different
        // sockets, different code paths), and the AS metadata fetch hits a different host
        // entirely. We deliberately keep the auth scan sequential (it already ran above and
        // the audit needs its result to pick a profile for tool enumeration).
        var transportTask = _transportProbe.ProbeAsync(server.Url, cancellationToken);
        var inspectionTask = InspectViaBestAvailableAuthAsync(server, auth, profiles, authOverrides, handshakeTimeout, cancellationToken);

        var transport = await transportTask.ConfigureAwait(false);
        var inspection = await inspectionTask.ConfigureAwait(false);

        var security = BuildSecuritySummary(server, transport);
        var oauth = await BuildOAuthSummaryAsync(auth, checkAuthorizationServers, cancellationToken).ConfigureAwait(false);

        // Map the inspection result back into the per-section listings. When the inspection
        // failed entirely (no auth path worked), we leave fetched=false on each listing with
        // the same error so consumers see the consistent "we couldn't read this" signal.
        var tools = inspection.Success
            ? new ToolListing(true, inspection.FetchedVia, null, inspection.Tools)
            : EmptyToolListing(inspection.Error);
        var prompts = inspection.Success
            ? new PromptListing(true, inspection.FetchedVia, null, inspection.Prompts)
            : EmptyPromptListing(inspection.Error);
        var resources = inspection.Success
            ? new ResourceListing(true, inspection.FetchedVia, null, inspection.Resources, inspection.Templates)
            : EmptyResourceListing(inspection.Error);

        return new ServerAudit(
            Name: server.Name,
            Transport: "http",
            Target: server.Target,
            Auth: auth,
            ServerInfo: inspection.ServerInfo,
            Protocol: inspection.Protocol,
            Tools: tools,
            Prompts: prompts,
            Resources: resources,
            Security: security,
            OAuth: oauth,
            Behavior: new BehaviorProbes(CallNonExistentTool: inspection.CallNonExistentTool),
            Stdio: null);
    }

    /// <summary>
    /// Picks the best available auth path and runs the session inspection through it. Order:
    /// <list type="number">
    ///   <item>Anonymous, when the auth scan classified the server as <see cref="AuthClassifications.Anonymous"/>.</item>
    ///   <item>The first profile that succeeded in the auth scan's <see cref="ProfileAttempt"/> list.</item>
    ///   <item>If neither path is available, return a failure outcome so the audit can still
    ///         emit the rest of the report.</item>
    /// </list>
    /// This means an audit run produces at most ONE extra MCP session beyond what the auth
    /// scan already opened (the auth scan's anonymous-confirmation handshake and per-profile
    /// handshakes are connection-only; the inspection session is a separate, longer-lived
    /// session that also lists tools/prompts/resources and runs the non-existent-tool probe).
    /// </summary>
    private async Task<InspectionOutcome> InspectViaBestAvailableAuthAsync(
        ResolvedServer server,
        ServerAuthScan auth,
        IReadOnlyList<AuthProfile> profiles,
        AuthOverrides authOverrides,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        if (string.Equals(auth.Classification, AuthClassifications.Anonymous, StringComparison.Ordinal))
        {
            return await _sessionInspector.InspectAsync(
                server with { Auth = null },
                fetchedVia: "anonymous",
                handshakeTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        // Find the first successful profile attempt and use that profile's auth. We look up
        // the profile by name (rather than re-resolving from AuthOverrides) so the audit is
        // consistent with what the auth scan actually exercised.
        foreach (var attempt in auth.ProfileAttempts)
        {
            if (!attempt.Success)
            {
                continue;
            }

            var profile = profiles.FirstOrDefault(p => string.Equals(p.Name, attempt.ProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                continue;
            }

            // We don't re-substitute scopes here because the auth scan already did, and the
            // resulting scopes are captured on the ProfileAttempt. Use them directly so the
            // inspection runs with the exact same auth the scan validated.
            var resolvedAuth = profile.Auth with { Scopes = attempt.Scopes };
            return await _sessionInspector.InspectAsync(
                server with { Auth = resolvedAuth },
                fetchedVia: $"profile:{profile.Name}",
                handshakeTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        var noPathError = authOverrides.NoAuth || authOverrides.ClassifyOnly
            ? "No anonymous session available, and profile attempts were skipped (--no-auth / --classify-only)."
            : auth.ProfileAttempts.Count == 0
                ? "No anonymous session available and no profiles were loaded; load a profile with --profiles or --profile to enumerate tools/prompts/resources."
                : "No anonymous session and no profile authenticated; see auth.profileAttempts for individual failure details.";

        return new InspectionOutcome(
            Success: false,
            FetchedVia: null,
            Error: noPathError,
            ServerInfo: null,
            Protocol: null,
            Tools: [],
            Prompts: [],
            Resources: [],
            Templates: [],
            CallNonExistentTool: null);
    }

    private static SecuritySummary BuildSecuritySummary(ResolvedServer server, TransportProbeResult transport)
    {
        var mixedContent = string.Equals(server.Url!.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        return new SecuritySummary(
            MixedContent: mixedContent,
            Tls: transport.Tls,
            ResponseHeaders: transport.Headers);
    }

    private async Task<OAuthSummary?> BuildOAuthSummaryAsync(
        ServerAuthScan auth,
        bool checkAuthorizationServers,
        CancellationToken cancellationToken)
    {
        // OAuth section only makes sense when the server actually advertised OAuth. For
        // anonymous / bearer-unannounced / unspecified servers there's nothing to report.
        if (!string.Equals(auth.Classification, AuthClassifications.OAuthRfc9728, StringComparison.Ordinal))
        {
            return null;
        }

        // RFC 7591 dynamic client registration is advertised by the AS, not the PRM. We
        // surface the DCR endpoint at the OAuthSummary level only when --check-authorization-
        // servers is set AND we successfully fetched the AS metadata. Without that fetch we
        // can't know whether the AS advertises a registration_endpoint.
        var asEntries = new List<AuthorizationServerInfo>();
        if (checkAuthorizationServers && auth.Details.AuthorizationServers is { Count: > 0 })
        {
            foreach (var issuer in auth.Details.AuthorizationServers)
            {
                asEntries.Add(await _authServerProbe.ProbeAsync(issuer, cancellationToken).ConfigureAwait(false));
            }
        }

        // The DcrInfo summary picks the most informative registration_endpoint observation:
        // if any fetched AS advertised one, surface it; otherwise null. OpenRegistration is
        // left null because verifying it would require actually attempting a registration,
        // which is out of scope (it's a write operation).
        DcrInfo? dcr = null;
        foreach (var entry in asEntries)
        {
            if (!string.IsNullOrEmpty(entry.RegistrationEndpoint))
            {
                dcr = new DcrInfo(Endpoint: entry.RegistrationEndpoint, OpenRegistration: null);
                break;
            }
        }

        return new OAuthSummary(
            DcrFromResourceMetadata: dcr,
            AuthorizationServers: asEntries);
    }

    private static ToolListing EmptyToolListing(string? error)
        => new(Fetched: false, FetchedVia: null, FetchError: error, Items: []);

    private static PromptListing EmptyPromptListing(string? error)
        => new(Fetched: false, FetchedVia: null, FetchError: error, Items: []);

    private static ResourceListing EmptyResourceListing(string? error)
        => new(Fetched: false, FetchedVia: null, FetchError: error, Items: [], Templates: []);
}
