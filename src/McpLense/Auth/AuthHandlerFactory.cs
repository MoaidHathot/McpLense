using Azure.Identity;

namespace McpLense;

/// <summary>
/// Maps a <see cref="ResolvedAuth"/> to an <see cref="HttpMessageHandler"/> chain that wraps
/// outbound HTTP requests. Returns <c>null</c> for <see cref="AuthKind.None"/> so callers can
/// skip the authenticated <see cref="HttpClient"/> code path entirely.
/// </summary>
internal static class AuthHandlerFactory
{
    /// <summary>Default MSAL cache file name when no per-server override is supplied.</summary>
    internal const string DefaultInteractiveBrowserCacheName = "mcplense";

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
            AuthKind.InteractiveBrowser => CreateInteractiveBrowser(auth),
            AuthKind.AzureCli => CreateAzureCli(auth),
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

    private static DelegatingHandler CreateInteractiveBrowser(ResolvedAuth auth)
    {
        var credential = BuildInteractiveBrowserCredential(auth);
        return new InteractiveBrowserHandler(credential, auth.Scopes!);
    }

    private static DelegatingHandler CreateAzureCli(ResolvedAuth auth)
    {
        var credential = BuildAzureCliCredential(auth);
        return new InteractiveBrowserHandler(credential, auth.Scopes!);
    }

    /// <summary>
    /// Builds an <see cref="AzureCliCredential"/> from a resolved azure-cli auth block.
    /// Centralised here so the login/logout session paths can reuse the exact same options.
    /// </summary>
    internal static AzureCliCredential BuildAzureCliCredential(ResolvedAuth auth)
    {
        if (auth.Scopes is null || auth.Scopes.Count == 0)
        {
            throw new McpLenseAuthException(
                "Azure CLI authentication requires at least one scope. " +
                "Add 'auth.scopes' to the profile.");
        }

        var options = new AzureCliCredentialOptions();

        if (!string.IsNullOrEmpty(auth.TenantId))
        {
            options.TenantId = auth.TenantId;
        }

        return new AzureCliCredential(options);
    }

    /// <summary>
    /// Builds an <see cref="InteractiveBrowserCredential"/> from a resolved interactive-browser
    /// auth block. Centralised here so <see cref="InteractiveBrowserSessionRunner"/> can reuse the
    /// exact same composition for <c>--login</c>/<c>--logout</c>.
    /// </summary>
    internal static InteractiveBrowserCredential BuildInteractiveBrowserCredential(ResolvedAuth auth)
    {
        if (string.IsNullOrEmpty(auth.ClientId))
        {
            throw new McpLenseAuthException(
                "Interactive-browser authentication requires a non-empty 'clientId'. " +
                "Set it via the config 'auth.clientId' field or '--client-id'.");
        }

        if (auth.Scopes is null || auth.Scopes.Count == 0)
        {
            throw new McpLenseAuthException(
                "Interactive-browser authentication requires at least one scope. " +
                "Add 'auth.scopes' to the config or pass '--scope'.");
        }

        var options = new InteractiveBrowserCredentialOptions
        {
            ClientId = auth.ClientId,
            // TokenCachePersistenceOptions enables MSAL's encrypted on-disk cache. Default path on
            // Windows: %LOCALAPPDATA%\.IdentityService\<Name>. We default Name to "mcplense" so
            // mcplense never accidentally pollutes another tool's cache; setting cacheName to
            // "mcp-proxy" in config opts into a shared cache with that tool.
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = string.IsNullOrEmpty(auth.CacheName) ? DefaultInteractiveBrowserCacheName : auth.CacheName
            }
        };

        if (!string.IsNullOrEmpty(auth.TenantId))
        {
            options.TenantId = auth.TenantId;
        }

        if (!string.IsNullOrEmpty(auth.RedirectUri))
        {
            if (!Uri.TryCreate(auth.RedirectUri, UriKind.Absolute, out var redirect))
            {
                throw new McpLenseAuthException(
                    $"Interactive-browser 'redirectUri' must be an absolute URI but was '{auth.RedirectUri}'.");
            }

            options.RedirectUri = redirect;
        }

        return new InteractiveBrowserCredential(options);
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
