namespace McpLense;

/// <summary>
/// Top-level outcome record for the <c>--login</c> and <c>--logout</c> CLI flags.
/// </summary>
/// <param name="Action">Either <c>"login"</c> or <c>"logout"</c>.</param>
/// <param name="GeneratedAt">UTC timestamp the report was produced.</param>
/// <param name="Servers">One entry per resolved HTTP MCP server.</param>
internal sealed record AuthSessionReport(
    string Action,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AuthSessionEntry> Servers);

/// <summary>
/// Per-server status returned from a login/logout action.
/// </summary>
/// <param name="Name">Server name as resolved from the config or CLI.</param>
/// <param name="Target">Server target URL.</param>
/// <param name="Success">True when the action succeeded for this server.</param>
/// <param name="Detail">
/// Human-readable detail (e.g. <c>"cached token at 2024-01-01T00:00:00Z"</c> on login,
/// <c>"removed cache entry"</c> on logout, or <c>"no cache entry to remove"</c>).
/// </param>
/// <param name="Error">Error message when <see cref="Success"/> is false.</param>
internal sealed record AuthSessionEntry(
    string Name,
    string Target,
    bool Success,
    string? Detail = null,
    string? Error = null);

/// <summary>
/// Drives the top-level <c>--login</c> and <c>--logout</c> short-circuit paths.
/// Builds the same orchestrator/cache stack used by the runtime <see cref="OAuthDiscoveryHandler"/>
/// so cache keys and discovery semantics line up exactly.
/// </summary>
internal static class AuthSessionRunner
{
    public static async Task<AuthSessionReport> LoginAsync(IReadOnlyList<ResolvedServer> servers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var entries = new List<AuthSessionEntry>(servers.Count);
        foreach (var server in servers)
        {
            entries.Add(await LoginOneAsync(server, cancellationToken).ConfigureAwait(false));
        }

        return new AuthSessionReport("login", DateTimeOffset.UtcNow, entries);
    }

    public static async Task<AuthSessionReport> LogoutAsync(IReadOnlyList<ResolvedServer> servers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var cache = new OAuthTokenCache();
        var entries = new List<AuthSessionEntry>(servers.Count);

        foreach (var server in servers)
        {
            entries.Add(await LogoutOneAsync(server, cache, cancellationToken).ConfigureAwait(false));
        }

        return new AuthSessionReport("logout", DateTimeOffset.UtcNow, entries);
    }

    private static async Task<AuthSessionEntry> LoginOneAsync(ResolvedServer server, CancellationToken cancellationToken)
    {
        var validation = ValidateOAuthServer(server, "login");
        if (validation is not null)
        {
            return validation;
        }

        var auth = server.Auth!;
        var resourceUri = ResolveResourceUri(auth, server.Url!);

        using var http = new HttpClient(new SocketsHttpHandler(), disposeHandler: true);
        var cache = new OAuthTokenCache();
        var browser = new SystemBrowserLauncher();
        var orchestrator = new OAuthFlowOrchestrator(http, cache, browser);

        try
        {
            var entry = await orchestrator.RunInteractiveAsync(auth, resourceUri, cancellationToken).ConfigureAwait(false);
            var detail = entry.ExpiresAt is { } expiresAt
                ? $"cached token expires at {expiresAt:O}"
                : "cached non-expiring token";

            return new AuthSessionEntry(server.Name, server.Target, Success: true, Detail: detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AuthSessionEntry(server.Name, server.Target, Success: false, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<AuthSessionEntry> LogoutOneAsync(ResolvedServer server, OAuthTokenCache cache, CancellationToken cancellationToken)
    {
        var validation = ValidateOAuthServer(server, "logout");
        if (validation is not null)
        {
            return validation;
        }

        var auth = server.Auth!;
        var resourceUri = ResolveResourceUri(auth, server.Url!);
        var cacheKey = IOAuthTokenCache.ResolveCacheKey(auth.CacheName, resourceUri.ToString());

        try
        {
            var removed = await cache.DeleteAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            var detail = removed ? "removed cache entry" : "no cache entry to remove";
            return new AuthSessionEntry(server.Name, server.Target, Success: true, Detail: detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AuthSessionEntry(server.Name, server.Target, Success: false, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns null when the server is a valid OAuth target; otherwise an error <see cref="AuthSessionEntry"/>.
    /// We intentionally surface validation as a per-server failure (instead of throwing) so a single
    /// stdio entry in a multi-server config does not abort login/logout for the rest.
    /// </summary>
    private static AuthSessionEntry? ValidateOAuthServer(ResolvedServer server, string action)
    {
        if (server.Kind != ConnectionKind.Http || server.Url is null)
        {
            return new AuthSessionEntry(
                server.Name,
                server.Target,
                Success: false,
                Error: $"--{action} only applies to HTTP/SSE servers; '{server.Name}' is a stdio target.");
        }

        if (server.Auth is null || server.Auth.Kind != AuthKind.OAuth)
        {
            return new AuthSessionEntry(
                server.Name,
                server.Target,
                Success: false,
                Error: $"--{action} requires OAuth authentication on '{server.Name}'. Add 'auth.type: oauth' or pass '--auth oauth'.");
        }

        return null;
    }

    private static Uri ResolveResourceUri(ResolvedAuth auth, Uri serverUrl)
    {
        if (string.IsNullOrEmpty(auth.ResourceUri))
        {
            return serverUrl;
        }

        if (!Uri.TryCreate(auth.ResourceUri, UriKind.Absolute, out var parsed))
        {
            throw new McpLenseAuthException(
                $"OAuth 'resourceUri' must be an absolute URI but was '{auth.ResourceUri}'.");
        }

        return parsed;
    }
}
