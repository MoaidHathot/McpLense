namespace McpLense;

/// <summary>
/// Discovery-only command driver: classifies the authentication model of one or more MCP servers
/// and (when profiles are loaded) reports which profile(s) actually succeed at opening a session.
/// Never modifies anything - it only sends GETs to the protected-resource metadata endpoints and a
/// single MCP <c>initialize</c> handshake per attempt.
/// </summary>
/// <remarks>
/// <para>
/// The flow is split across three collaborators: <see cref="AuthDiscovery"/> performs the RFC 9728
/// probe (and scope substitution), <see cref="AuthClassifier"/> turns the gathered signals into a
/// classification, and this orchestrator owns the per-server loop, the conditional unauthenticated
/// handshake, and the profile attempts. Classification preference order (anonymous-via-handshake,
/// RFC 9728, unannounced Bearer, unspecified, unknown) lives in <see cref="AuthClassifier"/>.
/// </para>
/// <para>
/// Profile attempts are independent of the classification: if any profiles are loaded (and
/// <c>--no-auth</c> is not set), the scanner tries every selected profile against every HTTP server,
/// so the report can show "profile X works against server Y" even when the probe alone couldn't tell.
/// </para>
/// </remarks>
internal sealed class AuthScanner
{
    private readonly AuthDiscovery _discovery;
    private readonly IMcpHandshakeProbe _handshake;

    public AuthScanner(IAuthProbe probe, IMcpHandshakeProbe handshake)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _discovery = new AuthDiscovery(probe);
        _handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
    }

    /// <summary>
    /// Convenience entry-point used by <see cref="McpExecutor"/>. Owns the default
    /// <see cref="AuthProbe"/> and <see cref="McpHandshakeProbe"/> instances.
    /// </summary>
    public static async Task<AuthScanReport> ScanAsync(
        IReadOnlyList<ResolvedServer> servers,
        IReadOnlyList<AuthProfile> profiles,
        AuthOverrides authOverrides,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        using var probe = new AuthProbe();
        var scanner = new AuthScanner(probe, new McpHandshakeProbe());
        return await scanner.ScanCoreAsync(servers, profiles, authOverrides, handshakeTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test-friendly entry-point with injected probe + handshake implementations. Public for unit-test
    /// access; production callers go through the static <see cref="ScanAsync"/> helper.
    /// </summary>
    public async Task<AuthScanReport> ScanCoreAsync(
        IReadOnlyList<ResolvedServer> servers,
        IReadOnlyList<AuthProfile> profiles,
        AuthOverrides authOverrides,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(authOverrides);

        var entries = new List<ServerAuthScan>(servers.Count);
        foreach (var server in servers)
        {
            entries.Add(await ScanOneAsync(server, profiles, authOverrides, handshakeTimeout, cancellationToken).ConfigureAwait(false));
        }

        return new AuthScanReport(DateTimeOffset.UtcNow, entries);
    }

    private async Task<ServerAuthScan> ScanOneAsync(
        ResolvedServer server,
        IReadOnlyList<AuthProfile> profiles,
        AuthOverrides authOverrides,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        // Stdio targets don't have HTTP-level auth - report and move on. We still emit an entry so a
        // "scan everything in my config" run produces one report row per server.
        if (server.Kind != ConnectionKind.Http || server.Url is null)
        {
            return new ServerAuthScan(
                Name: server.Name,
                Transport: "stdio",
                Target: server.Target,
                Classification: AuthClassifications.Stdio,
                Summary: "Stdio target - HTTP authentication does not apply.",
                Details: new AuthScanDetails(),
                ProfileAttempts: [],
                ServerStatus: ServerAccessibility.Accessible,
                Rfcs: []);
        }

        var classification = await ClassifyAsync(server, handshakeTimeout, cancellationToken).ConfigureAwait(false);

        // When the unauthenticated handshake already proved the server accepts anonymous sessions,
        // profile attempts add no information - they would 'succeed' for any server that simply
        // ignores the Authorization header, producing a misleading report line. Skip them by default;
        // the user can still run inspect/tools/... with an explicit --profile to exercise auth.
        IReadOnlyList<ProfileAttempt> attempts;
        if (string.Equals(classification.Classification, AuthClassifications.Anonymous, StringComparison.Ordinal))
        {
            attempts = [];
        }
        else
        {
            attempts = await TryProfilesAsync(server, profiles, authOverrides, handshakeTimeout, cancellationToken).ConfigureAwait(false);
        }

        return new ServerAuthScan(
            Name: server.Name,
            Transport: "http",
            Target: server.Target,
            Classification: classification.Classification,
            Summary: classification.Summary,
            Details: classification.Details,
            ProfileAttempts: attempts,
            ServerStatus: AuthClassifier.DeriveServerStatus(classification.Classification, classification.Details),
            Rfcs: AuthClassifier.DeriveRfcs(classification.Classification));
    }

    /// <summary>
    /// Gathers signals and classifies one HTTP server: probe first; if the probe carried an explicit
    /// auth challenge that's the answer, otherwise the unauthenticated MCP handshake is the
    /// authoritative tie-breaker. The decision logic lives in <see cref="AuthClassifier"/>; this just
    /// sequences the probe and the (conditional) handshake.
    /// </summary>
    private async Task<AuthClassification> ClassifyAsync(
        ResolvedServer server,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        var probeOutcome = await _discovery.ProbeAsync(server, cancellationToken).ConfigureAwait(false);
        if (probeOutcome.ProbeError is not null)
        {
            return AuthClassifier.FromProbeError(probeOutcome.ProbeError);
        }

        var probe = probeOutcome.Result!;
        var baseDetails = AuthClassifier.BuildBaseDetails(probe);

        var fromProbe = AuthClassifier.ClassifyFromProbe(probe, baseDetails);
        if (fromProbe is not null)
        {
            return fromProbe;
        }

        // The probe surfaced no auth challenge (clean 2xx, or an inconclusive non-2xx like a 405 on
        // a POST-only JSON-RPC endpoint). One unauthenticated `initialize` POST is the definitive test.
        var anonHandshake = await _handshake.TryHandshakeAsync(
            server with { Auth = null },
            handshakeTimeout,
            cancellationToken).ConfigureAwait(false);

        return AuthClassifier.ClassifyAfterHandshake(probe, baseDetails, anonHandshake);
    }

    private async Task<IReadOnlyList<ProfileAttempt>> TryProfilesAsync(
        ResolvedServer server,
        IReadOnlyList<AuthProfile> profiles,
        AuthOverrides authOverrides,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        // Profile attempts are skipped when:
        //   1. No profiles loaded (nothing to try).
        //   2. --no-auth was supplied (broad "no Authorization header anywhere" opt-out).
        //   3. --classify-only was supplied (scan-specific "just tell me the auth model").
        if (authOverrides.NoAuth || authOverrides.ClassifyOnly || profiles.Count == 0)
        {
            return [];
        }

        // --profile <name> picks exactly one candidate; missing names surface as a per-server error
        // so a multi-server scan still produces output for the others.
        IEnumerable<AuthProfile> candidates;
        if (!string.IsNullOrEmpty(authOverrides.Profile))
        {
            var match = profiles.FirstOrDefault(p => string.Equals(p.Name, authOverrides.Profile, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var available = string.Join(", ", profiles.Select(p => p.Name));
                return
                [
                    new ProfileAttempt(
                        ProfileName: authOverrides.Profile,
                        AuthKind: "unknown",
                        Scopes: null,
                        Success: false,
                        Error: $"--profile '{authOverrides.Profile}' was not found. Loaded profiles: {available}.")
                ];
            }

            candidates = new[] { match };
        }
        else
        {
            // Default: try every loaded profile in source order. Deterministic + complete.
            candidates = profiles;
        }

        var attempts = new List<ProfileAttempt>();
        foreach (var profile in candidates)
        {
            attempts.Add(await TryOneProfileAsync(server, profile, authOverrides.DefaultScope, handshakeTimeout, cancellationToken).ConfigureAwait(false));
        }

        return attempts;
    }

    private async Task<ProfileAttempt> TryOneProfileAsync(
        ResolvedServer server,
        AuthProfile profile,
        string? defaultScopeFallback,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        ResolvedAuth substitutedAuth;
        try
        {
            substitutedAuth = await _discovery.SubstituteScopesAsync(
                profile.Auth,
                server.Url!,
                defaultScopeFallback,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProfileAttempt(
                ProfileName: profile.Name,
                AuthKind: AuthClassifier.AuthKindToString(profile.Auth.Kind),
                Scopes: profile.Auth.Scopes,
                Success: false,
                Error: $"Failed to prepare auth from profile: {ex.GetType().Name}: {ex.Message}");
        }

        var serverWithAuth = server with { Auth = substitutedAuth };
        var handshake = await _handshake.TryHandshakeAsync(serverWithAuth, handshakeTimeout, cancellationToken).ConfigureAwait(false);

        if (handshake.Success)
        {
            return new ProfileAttempt(
                ProfileName: profile.Name,
                AuthKind: AuthClassifier.AuthKindToString(profile.Auth.Kind),
                Scopes: substitutedAuth.Scopes,
                Success: true,
                Detail: AuthClassifier.FormatCapabilitySummary(handshake),
                ToolCount: handshake.ToolCount,
                ResourceCount: handshake.ResourceCount,
                PromptCount: handshake.PromptCount);
        }

        return new ProfileAttempt(
            ProfileName: profile.Name,
            AuthKind: AuthClassifier.AuthKindToString(profile.Auth.Kind),
            Scopes: substitutedAuth.Scopes,
            Success: false,
            Error: handshake.Error);
    }

    // Thin forwarders kept so existing callers/tests (AuthScannerDerivationTests) keep their entry
    // point; the implementations now live in AuthClassifier.
    internal static string DeriveServerStatus(string classification, AuthScanDetails details)
        => AuthClassifier.DeriveServerStatus(classification, details);

    internal static IReadOnlyList<string> DeriveRfcs(string classification)
        => AuthClassifier.DeriveRfcs(classification);
}
