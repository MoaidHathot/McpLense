namespace McpLense;

/// <summary>
/// Discovery-only command driver: classifies the authentication model of one or more MCP
/// servers and (when profiles are loaded) reports which profile(s) actually succeed at opening
/// a session. Never modifies anything - the scanner only sends GETs to the protected-resource
/// metadata endpoints and a single MCP <c>initialize</c> handshake per attempt.
/// </summary>
/// <remarks>
/// <para>
/// Classification preference order:
/// <list type="number">
///   <item><description><see cref="ConnectionKind.Stdio"/> targets are reported as
///   <see cref="AuthClassifications.Stdio"/> with no further work.</description></item>
///   <item><description>The probe's <c>Inconclusive</c> signal (network failure, 5xx, ...) →
///   <see cref="AuthClassifications.Unknown"/>.</description></item>
///   <item><description><c>RequiresAuth=true</c> with a non-empty <c>scopes_supported</c> /
///   <c>resource_metadata</c> → <see cref="AuthClassifications.OAuthRfc9728"/>.</description></item>
///   <item><description><c>RequiresAuth=true</c> with a Bearer challenge but no metadata →
///   <see cref="AuthClassifications.OAuthBearerUnannounced"/>.</description></item>
///   <item><description><c>RequiresAuth=true</c> with a non-Bearer scheme →
///   <see cref="AuthClassifications.AuthRequiredUnspecified"/>.</description></item>
///   <item><description>Probe surfaced no auth signal at all. We then attempt a no-auth MCP
///   <c>initialize</c> handshake: if it succeeds the server is genuinely
///   <see cref="AuthClassifications.Anonymous"/>; if it fails with what looks like an auth
///   error we downgrade to <see cref="AuthClassifications.AuthRequiredUnspecified"/>;
///   otherwise <see cref="AuthClassifications.Unknown"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// Profile attempts are independent of the classification: if any profiles are loaded (and
/// <c>--no-auth</c> is not set), the scanner tries every selected profile against every HTTP
/// server, so the report can show "profile X works against server Y" even when the probe alone
/// couldn't tell.
/// </para>
/// </remarks>
internal sealed class AuthScanner
{
    private readonly IAuthProbe _probe;
    private readonly IMcpHandshakeProbe _handshake;

    public AuthScanner(IAuthProbe probe, IMcpHandshakeProbe handshake)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
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
    /// Test-friendly entry-point with injected probe + handshake implementations. Public for
    /// unit-test access; production callers go through the static <see cref="ScanAsync"/>
    /// helper.
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
        // Stdio targets don't have HTTP-level auth - report and move on. We still emit an entry
        // so a "scan everything in my config" run produces one report row per server.
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

        var (classification, summary, details) = await ClassifyAsync(server, handshakeTimeout, cancellationToken).ConfigureAwait(false);

        // When the unauthenticated handshake already proved the server accepts anonymous
        // sessions, profile attempts add no information - they would 'succeed' for any
        // server that simply ignores the Authorization header on inbound requests, producing
        // a misleading "this profile authenticates this server" line in the report. Skip
        // them by default; the user can still run `mcplense inspect/tools/...` with an
        // explicit --profile when they want to exercise an authenticated path.
        IReadOnlyList<ProfileAttempt> attempts;
        if (string.Equals(classification, AuthClassifications.Anonymous, StringComparison.Ordinal))
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
            Classification: classification,
            Summary: summary,
            Details: details,
            ProfileAttempts: attempts,
            ServerStatus: DeriveServerStatus(classification, details),
            Rfcs: DeriveRfcs(classification));
    }

    /// <summary>
    /// Coarse, consumer-facing reachability label derived from the raw probe signals + the
    /// final classification. See <see cref="ServerAccessibility"/> for the stable wire values.
    /// Centralising the derivation here means every fleet consumer sees the same answer
    /// instead of re-implementing the (status-code, www-authenticate, handshake) decision tree.
    /// </summary>
    internal static string DeriveServerStatus(string classification, AuthScanDetails details)
    {
        // 1. Classification wins for the unambiguous cases.
        switch (classification)
        {
            case AuthClassifications.Stdio:
            case AuthClassifications.Anonymous:
                return ServerAccessibility.Accessible;

            case AuthClassifications.OAuthRfc9728:
            case AuthClassifications.OAuthBearerUnannounced:
            case AuthClassifications.AuthRequiredUnspecified:
                return ServerAccessibility.RequiresAuth;
        }

        // 2. Unknown classification - inspect the raw signals.
        if (details.StatusCode is { } status)
        {
            if (status is 404 or 410)
            {
                return ServerAccessibility.NotFound;
            }
        }
        else if (!string.IsNullOrEmpty(details.ProbeError))
        {
            // No status code at all + probe error = network-level failure (DNS, TLS, connect,
            // timeout, ...). Distinguishable from "reached but inconclusive".
            return ServerAccessibility.Unreachable;
        }

        return ServerAccessibility.Unknown;
    }

    /// <summary>
    /// RFC numbers implicated by the classification. Returns an empty list for classifications
    /// that don't map to any specific RFC (anonymous, stdio, non-Bearer challenges, unknown).
    /// Consumers that need finer detail (e.g. distinguish DCR-supporting servers) should still
    /// pattern-match on the raw signals + the <c>dcrEndpoint</c> / <c>authorizationServers</c>
    /// check outputs.
    /// </summary>
    internal static IReadOnlyList<string> DeriveRfcs(string classification) => classification switch
    {
        AuthClassifications.OAuthRfc9728 => new[] { "RFC 9728", "RFC 6750", "RFC 8414" },
        AuthClassifications.OAuthBearerUnannounced => new[] { "RFC 6750" },
        _ => Array.Empty<string>()
    };

    private async Task<(string Classification, string Summary, AuthScanDetails Details)> ClassifyAsync(
        ResolvedServer server,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        AuthProbeResult probeResult;
        try
        {
            // Forward per-target headers (when scope=all) so a server that gates everything
            // behind, e.g. x-mcp-ec-organization, can still surface its RFC 9728 challenge.
            // Same-origin only - the metadata-document fetch inside the probe respects the
            // same-origin guard.
            probeResult = await _probe.ProbeAsync(
                server.Url!,
                server.Headers.Count == 0 ? null : server.Headers,
                server.HeaderScope,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The default AuthProbe swallows network errors internally and returns Inconclusive,
            // so this catch is defensive (e.g. a test stub that throws).
            var details = new AuthScanDetails(ProbeError: $"{ex.GetType().Name}: {ex.Message}");
            return (AuthClassifications.Unknown, "Probe threw an exception; classification unknown.", details);
        }

        // Build the per-classification details up-front so every branch reports the same raw
        // signals. Optional fields stay null when the probe didn't surface them.
        var baseDetails = new AuthScanDetails(
            StatusCode: probeResult.StatusCode,
            ReasonPhrase: probeResult.ReasonPhrase,
            WwwAuthenticate: probeResult.WwwAuthenticate,
            ResourceMetadataUrl: probeResult.ResourceMetadataUrl,
            Resource: probeResult.Resource,
            Scopes: probeResult.Scopes,
            AuthorizationServers: probeResult.AuthorizationServers,
            DiagnosticHeaders: probeResult.DiagnosticHeaders);

        // Branch 1: the probe surfaced an explicit auth challenge (401 / WWW-Authenticate).
        // No need to attempt an unauthenticated MCP handshake - the server has told us
        // credentials are required.
        if (probeResult.RequiresAuth)
        {
            if (!string.IsNullOrEmpty(probeResult.ResourceMetadataUrl))
            {
                var summary = (probeResult.Scopes is { Count: > 0 })
                    ? $"OAuth via RFC 9728 - {probeResult.Scopes.Count} scope(s) advertised at {probeResult.ResourceMetadataUrl}."
                    : $"OAuth via RFC 9728 - metadata pointer at {probeResult.ResourceMetadataUrl} (no scopes_supported in the document).";
                return (AuthClassifications.OAuthRfc9728, summary, baseDetails);
            }

            if (HasBearerChallenge(probeResult.WwwAuthenticate))
            {
                return (
                    AuthClassifications.OAuthBearerUnannounced,
                    "Server demands Bearer auth but does not advertise RFC 9728 protected-resource metadata.",
                    baseDetails);
            }

            var headerHint = string.IsNullOrEmpty(probeResult.WwwAuthenticate)
                ? "no WWW-Authenticate header"
                : $"scheme '{probeResult.WwwAuthenticate}'";
            return (
                AuthClassifications.AuthRequiredUnspecified,
                $"Server demands authentication but uses an unrecognised challenge ({headerHint}).",
                baseDetails);
        }

        // Branch 2: the probe did NOT surface an auth challenge. This covers two distinct
        // cases that need the same follow-up:
        //
        //   a) Clean 2xx (probeResult.IsEmpty): the server responded happily to an
        //      unauthenticated GET. Could be a genuinely anonymous MCP, OR an MCP that gates
        //      auth at the protocol level above the HTTP layer.
        //
        //   b) Inconclusive (probeResult.Inconclusive): the server returned a non-2xx (405
        //      / 404 / 5xx) without an auth challenge. Real MCP endpoints commonly reject
        //      `GET` on their JSON-RPC URL (MCP only accepts `POST`), so 405 here doesn't
        //      mean "broken" - it means the GET probe can't tell us anything.
        //
        // The only authoritative test in either case is to attempt the actual MCP
        // `initialize` handshake without credentials. One POST, one definitive answer.
        var anonHandshake = await _handshake.TryHandshakeAsync(
            server with { Auth = null },
            handshakeTimeout,
            cancellationToken).ConfigureAwait(false);

        var detailsWithHandshake = baseDetails with
        {
            AnonymousHandshakeSucceeded = anonHandshake.Success,
            AnonymousHandshakeError = anonHandshake.Error
        };

        if (anonHandshake.Success)
        {
            return (
                AuthClassifications.Anonymous,
                "Server accepts unauthenticated MCP sessions.",
                detailsWithHandshake);
        }

        // Handshake failed. If the error looks like auth (401/403/Unauthorized/Forbidden),
        // we have enough signal to call out "auth-required-unspecified" even though the GET
        // probe missed it.
        if (LooksLikeAuthError(anonHandshake.Error))
        {
            return (
                AuthClassifications.AuthRequiredUnspecified,
                "Server rejected the unauthenticated MCP handshake with what looks like an auth error (the GET probe did not surface this challenge).",
                detailsWithHandshake);
        }

        // Handshake failed for non-auth reasons (transport mismatch, server bug, wrong URL,
        // probe-already-inconclusive plus a real outage, ...). Surface the failure detail in
        // the report and label the result Unknown so the user knows to investigate without
        // assuming credentials are needed.
        var summaryWhenUnknown = probeResult.Inconclusive
            ? (probeResult.StatusCode is { } status
                ? $"Probe returned {status} without an auth challenge and the unauthenticated MCP handshake also failed; classification inconclusive."
                : "Probe could not reach the server and the unauthenticated MCP handshake also failed.")
            : "Probe surfaced no auth signal and the unauthenticated MCP handshake failed for non-auth reasons.";

        return (AuthClassifications.Unknown, summaryWhenUnknown, detailsWithHandshake);
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
        // Both flags reach the same end-state for scan; we honour them both so users can
        // discover the behaviour via either name.
        if (authOverrides.NoAuth || authOverrides.ClassifyOnly || profiles.Count == 0)
        {
            return [];
        }

        // --profile <name> picks exactly one candidate; the user knows what they want and we
        // don't probe the rest. Missing names surface as a per-server error so a multi-server
        // scan still produces output for the others.
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
            // Reuse the runtime scope-substitution logic so a probe-aware profile picks up the
            // same scopes that 'inspect' would. This is critical for Entra-style profiles whose
            // configured scope is "<audience>/.default" - the probe usually has a better match.
            // defaultScopeFallback covers AAD-backed MCPs that don't speak PRM at all.
            substitutedAuth = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(
                profile.Auth,
                server.Url!,
                _probe,
                cancellationToken,
                defaultScopeFallback: defaultScopeFallback).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProfileAttempt(
                ProfileName: profile.Name,
                AuthKind: AuthKindToString(profile.Auth.Kind),
                Scopes: profile.Auth.Scopes,
                Success: false,
                Error: $"Failed to prepare auth from profile: {ex.GetType().Name}: {ex.Message}");
        }

        var serverWithAuth = server with { Auth = substitutedAuth };
        var handshake = await _handshake.TryHandshakeAsync(serverWithAuth, handshakeTimeout, cancellationToken).ConfigureAwait(false);

        if (handshake.Success)
        {
            var detail = FormatCapabilitySummary(handshake);
            return new ProfileAttempt(
                ProfileName: profile.Name,
                AuthKind: AuthKindToString(profile.Auth.Kind),
                Scopes: substitutedAuth.Scopes,
                Success: true,
                Detail: detail,
                ToolCount: handshake.ToolCount,
                ResourceCount: handshake.ResourceCount,
                PromptCount: handshake.PromptCount);
        }

        return new ProfileAttempt(
            ProfileName: profile.Name,
            AuthKind: AuthKindToString(profile.Auth.Kind),
            Scopes: substitutedAuth.Scopes,
            Success: false,
            Error: handshake.Error);
    }

    private static string FormatCapabilitySummary(HandshakeResult handshake)
    {
        var parts = new List<string>();
        if (handshake.ToolCount is { } t)
        {
            parts.Add($"{t} tool(s)");
        }

        if (handshake.ResourceCount is { } r)
        {
            parts.Add($"{r} resource(s)");
        }

        if (handshake.PromptCount is { } p)
        {
            parts.Add($"{p} prompt(s)");
        }

        return parts.Count == 0
            ? "Handshake succeeded; no capability lists available."
            : "Handshake succeeded: " + string.Join(", ", parts) + ".";
    }

    private static bool HasBearerChallenge(string? wwwAuthenticate)
    {
        if (string.IsNullOrEmpty(wwwAuthenticate))
        {
            return false;
        }

        // The header is a comma-separated list of challenges; "Bearer" anywhere as a scheme
        // counts. Be lenient about case to match RFC 7235.
        foreach (var part in wwwAuthenticate.Split(','))
        {
            var trimmed = part.TrimStart();
            if (trimmed.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                // Make sure it's the scheme token, not "Bearertoken" or similar - the scheme is
                // followed by whitespace, end-of-string, or a parameter list.
                if (trimmed.Length == "Bearer".Length
                    || char.IsWhiteSpace(trimmed["Bearer".Length])
                    || trimmed["Bearer".Length] == ',')
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Heuristic match for "this exception message describes a 401/403 response from the MCP
    /// server". Used to downgrade an anonymous-probe verdict to "auth required" when the actual
    /// MCP handshake says otherwise. False positives are tolerable (the user just sees a more
    /// suggestive classification) but false negatives mean we leave a real auth-required server
    /// labelled "Unknown".
    /// </summary>
    private static bool LooksLikeAuthError(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return false;
        }

        return error.Contains("401", StringComparison.Ordinal)
               || error.Contains("403", StringComparison.Ordinal)
               || error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
               || error.Contains("authentication", StringComparison.OrdinalIgnoreCase);
    }

    private static string AuthKindToString(AuthKind kind) => kind switch
    {
        AuthKind.Bearer => "bearer",
        AuthKind.OAuth => "oauth",
        AuthKind.InteractiveBrowser => "interactive-browser",
        AuthKind.AzureCli => "azure-cli",
        AuthKind.None => "none",
        _ => kind.ToString().ToLowerInvariant()
    };
}
