namespace McpLense;

/// <summary>
/// Maps a <see cref="ResolvedAuth"/> to an <see cref="HttpMessageHandler"/> chain that wraps
/// outbound HTTP requests. Returns <c>null</c> for <see cref="AuthKind.None"/> so callers can
/// skip the authenticated <see cref="HttpClient"/> code path entirely.
/// </summary>
internal static class AuthHandlerFactory
{
    /// <summary>
    /// Builds the auth-aware <see cref="HttpMessageHandler"/> chain for the supplied
    /// <see cref="ResolvedAuth"/>. The returned handler still needs an
    /// <see cref="DelegatingHandler.InnerHandler"/> attached before being passed to
    /// <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="auth">Resolved per-server auth configuration.</param>
    /// <param name="serverUrl">
    /// MCP server endpoint URL. Used as the default RFC 8707 resource indicator and
    /// Protected Resource Metadata base when <see cref="ResolvedAuth.ResourceUri"/> is null.
    /// Required for <see cref="AuthKind.OAuth"/>; ignored for other kinds.
    /// </param>
    /// <returns>
    /// A <see cref="DelegatingHandler"/> for kinds that need outbound interception,
    /// or <c>null</c> when no handler chain is required.
    /// </returns>
    /// <exception cref="McpLenseAuthException">
    /// Thrown when required fields are missing or the auth kind is unsupported.
    /// </exception>
    public static DelegatingHandler? Create(ResolvedAuth auth, Uri? serverUrl = null)
    {
        if (auth is null)
        {
            return null;
        }

        return auth.Kind switch
        {
            AuthKind.None => null,
            AuthKind.Bearer => CreateBearer(auth),
            AuthKind.OAuth => CreateOAuth(auth, serverUrl),
            _ => throw new McpLenseAuthException($"Unsupported authentication kind '{auth.Kind}'.")
        };
    }

    private static DelegatingHandler CreateBearer(ResolvedAuth auth)
    {
        if (string.IsNullOrEmpty(auth.Token))
        {
            throw new McpLenseAuthException("Bearer authentication requires a non-empty token.");
        }

        return new BearerHandler(auth.Token);
    }

    private static DelegatingHandler CreateOAuth(ResolvedAuth auth, Uri? serverUrl)
    {
        var resourceUri = ResolveResourceUri(auth, serverUrl);

        // Dedicated HttpClient for discovery + token-endpoint traffic. Owned by the returned
        // handler so it is disposed alongside the wider chain.
        var orchestratorHttp = new HttpClient(new SocketsHttpHandler(), disposeHandler: true);
        var cache = new OAuthTokenCache();
        var browser = new SystemBrowserLauncher();
        var orchestrator = new OAuthFlowOrchestrator(orchestratorHttp, cache, browser);

        return new OAuthDiscoveryHandler(
            auth,
            resourceUri,
            cache,
            orchestrator,
            ownedResource: orchestratorHttp);
    }

    /// <summary>
    /// Resolves the RFC 8707 resource indicator to advertise to the authorization server.
    /// Prefers the explicit <see cref="ResolvedAuth.ResourceUri"/> override, falling back to the
    /// MCP server URL itself.
    /// </summary>
    private static Uri ResolveResourceUri(ResolvedAuth auth, Uri? serverUrl)
    {
        if (!string.IsNullOrEmpty(auth.ResourceUri))
        {
            if (!Uri.TryCreate(auth.ResourceUri, UriKind.Absolute, out var explicitUri))
            {
                throw new McpLenseAuthException(
                    $"OAuth 'resourceUri' must be an absolute URI but was '{auth.ResourceUri}'.");
            }

            return explicitUri;
        }

        if (serverUrl is null)
        {
            throw new McpLenseAuthException(
                "OAuth authentication requires either an explicit 'auth.resourceUri' or an HTTP server URL.");
        }

        return serverUrl;
    }
}
