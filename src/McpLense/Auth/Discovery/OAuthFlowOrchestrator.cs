using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpLense;

/// <summary>
/// Drives the full MCP OAuth 2.1 + DCR + PKCE flow end-to-end:
///
/// <list type="number">
///   <item>Resolve PRM (RFC 9728) and ASM (RFC 8414), unless static endpoints are supplied.</item>
///   <item>Reuse cached <c>client_id</c> if available, otherwise perform DCR (RFC 7591).</item>
///   <item>Generate PKCE verifier/challenge and a random <c>state</c>.</item>
///   <item>Bind a loopback <see cref="IOAuthCallbackListener"/> on the configured port.</item>
///   <item>Open the authorization URL in the default browser via <see cref="IBrowserLauncher"/>.</item>
///   <item>Exchange the returned <c>code</c> at the token endpoint with PKCE + RFC 8707 <c>resource</c>.</item>
///   <item>Cache the resulting tokens (and DCR creds) under the resolved cache key.</item>
/// </list>
///
/// All HTTP egress flows through the injected <see cref="HttpClient"/> so tests can intercept it
/// with an in-memory transport.
/// </summary>
internal sealed class OAuthFlowOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly OAuthDiscoveryClient _discovery;
    private readonly DynamicClientRegistration _dcr;
    private readonly IOAuthTokenCache _cache;
    private readonly IBrowserLauncher _browser;
    private readonly Func<string, IOAuthCallbackListener> _listenerFactory;
    private readonly TextWriter _stderr;

    public OAuthFlowOrchestrator(
        HttpClient http,
        IOAuthTokenCache cache,
        IBrowserLauncher browser,
        Func<string, IOAuthCallbackListener>? listenerFactory = null,
        TextWriter? stderr = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _discovery = new OAuthDiscoveryClient(_http);
        _dcr = new DynamicClientRegistration(_http);
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _listenerFactory = listenerFactory ?? (preferred => new HttpListenerCallback(preferred));
        _stderr = stderr ?? Console.Error;
    }

    /// <summary>
    /// Runs the full flow and returns the resulting cache entry. Also writes it to the cache.
    /// </summary>
    public async Task<OAuthCacheEntry> RunInteractiveAsync(
        ResolvedAuth auth,
        Uri resourceUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(resourceUri);

        var endpoints = await ResolveEndpointsAsync(auth, resourceUri, cancellationToken).ConfigureAwait(false);
        var cacheKey = IOAuthTokenCache.ResolveCacheKey(auth.CacheName, resourceUri.ToString());

        // Try to reuse the previously-cached client_id (and secret) so we don't re-register on every login.
        var existing = await _cache.LoadAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var (clientId, clientSecret) = await ResolveClientCredentialsAsync(auth, endpoints, existing, cancellationToken).ConfigureAwait(false);

        var preferredRedirect = auth.RedirectUri ?? "http://127.0.0.1:0/callback";
        using var listener = _listenerFactory(preferredRedirect);
        var redirectUri = listener.RedirectUri.ToString();

        var pkce = PkceHelper.Generate();
        var state = NewState();

        var authorizationUrl = BuildAuthorizationUrl(
            new Uri(endpoints.AuthorizationEndpoint),
            clientId,
            redirectUri,
            auth.Scopes,
            state,
            pkce.Challenge,
            resourceUri);

        var launched = _browser.TryLaunch(authorizationUrl);
        if (!launched)
        {
            _stderr.WriteLine();
            _stderr.WriteLine("Open this URL in a browser to complete authentication:");
            _stderr.WriteLine(authorizationUrl);
            _stderr.WriteLine();
        }

        var callback = await listener.WaitForCallbackAsync(state, cancellationToken).ConfigureAwait(false);

        var tokens = await ExchangeCodeAsync(
            new Uri(endpoints.TokenEndpoint),
            clientId,
            clientSecret,
            callback.Code,
            redirectUri,
            pkce.Verifier,
            resourceUri,
            cancellationToken).ConfigureAwait(false);

        var entry = BuildCacheEntry(auth, endpoints, redirectUri, clientId, clientSecret, resourceUri, tokens);
        await _cache.SaveAsync(cacheKey, entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    /// Refreshes the access token using the cached refresh token. Returns the updated cache
    /// entry on success, or null if the refresh fails (caller should fall back to interactive).
    /// The refreshed entry is written back to the cache under the same key the entry was loaded from.
    /// </summary>
    public async Task<OAuthCacheEntry?> TryRefreshAsync(
        OAuthCacheEntry entry,
        ResolvedAuth auth,
        Uri resourceUri,
        CancellationToken cancellationToken)
    {
        if (entry is null || string.IsNullOrEmpty(entry.RefreshToken))
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(resourceUri);

        try
        {
            var tokens = await CallTokenEndpointAsync(
                new Uri(entry.TokenEndpoint),
                BuildRefreshForm(entry, resourceUri),
                entry.ClientSecret,
                cancellationToken).ConfigureAwait(false);

            var refreshed = entry with
            {
                AccessToken = tokens.AccessToken ?? entry.AccessToken,
                RefreshToken = string.IsNullOrEmpty(tokens.RefreshToken) ? entry.RefreshToken : tokens.RefreshToken,
                ExpiresAt = ResolveExpiresAt(tokens),
                Scope = string.IsNullOrEmpty(tokens.Scope) ? entry.Scope : tokens.Scope
            };

            // Save under the same key the caller looked up so a custom auth.CacheName round-trips correctly.
            var cacheKey = IOAuthTokenCache.ResolveCacheKey(auth.CacheName, resourceUri.ToString());
            await _cache.SaveAsync(cacheKey, refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed;
        }
        catch (McpLenseAuthException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<EndpointBundle> ResolveEndpointsAsync(ResolvedAuth auth, Uri resourceUri, CancellationToken cancellationToken)
    {
        // Static endpoints win across the board; mix-and-match is fine.
        var authorizationEndpoint = auth.AuthorizationEndpoint;
        var tokenEndpoint = auth.TokenEndpoint;
        var registrationEndpoint = auth.RegistrationEndpoint;
        var issuer = auth.Issuer;

        if (authorizationEndpoint is not null && tokenEndpoint is not null)
        {
            // Caller provided everything we need.
            return new EndpointBundle(authorizationEndpoint, tokenEndpoint, registrationEndpoint, issuer);
        }

        // Otherwise discover. The PRM step yields the trusted issuer; ASM yields the rest.
        if (issuer is null)
        {
            var prmUri = auth.ResourceMetadataUrl is not null
                ? new Uri(auth.ResourceMetadataUrl)
                : OAuthDiscoveryClient.BuildPrmUri(resourceUri);

            var prm = await _discovery.FetchProtectedResourceMetadataAsync(prmUri, cancellationToken).ConfigureAwait(false);
            if (prm?.AuthorizationServers is { Count: > 0 })
            {
                issuer = prm.AuthorizationServers[0];
            }
        }

        if (issuer is null)
        {
            throw new McpLenseAuthException(
                $"Unable to discover OAuth issuer for '{resourceUri}'. Configure 'auth.issuer' or 'auth.authorizationEndpoint'/'auth.tokenEndpoint' explicitly.");
        }

        var asm = await _discovery.FetchAuthorizationServerMetadataAsync(new Uri(issuer), cancellationToken).ConfigureAwait(false);

        return new EndpointBundle(
            authorizationEndpoint ?? asm.AuthorizationEndpoint!,
            tokenEndpoint ?? asm.TokenEndpoint!,
            registrationEndpoint ?? asm.RegistrationEndpoint,
            issuer);
    }

    private async Task<(string ClientId, string? ClientSecret)> ResolveClientCredentialsAsync(
        ResolvedAuth auth,
        EndpointBundle endpoints,
        OAuthCacheEntry? existing,
        CancellationToken cancellationToken)
    {
        // Static client_id wins.
        if (!string.IsNullOrEmpty(auth.ClientId))
        {
            return (auth.ClientId, auth.ClientSecret);
        }

        // Cached DCR client_id is reused so we don't churn registrations.
        if (existing is not null && !string.IsNullOrEmpty(existing.ClientId))
        {
            return (existing.ClientId, existing.ClientSecret);
        }

        if (string.IsNullOrEmpty(endpoints.RegistrationEndpoint))
        {
            throw new McpLenseAuthException(
                "Authorization server does not advertise a 'registration_endpoint' and no 'auth.clientId' was supplied. " +
                "Set 'auth.clientId' (and optionally 'auth.clientSecret') in your config.");
        }

        var preferredRedirect = auth.RedirectUri ?? "http://127.0.0.1:0/callback";
        var dcrResponse = await _dcr.RegisterAsync(
            new Uri(endpoints.RegistrationEndpoint),
            preferredRedirect,
            auth.Scopes,
            cancellationToken).ConfigureAwait(false);

        return (dcrResponse.ClientId!, dcrResponse.ClientSecret);
    }

    private static Uri BuildAuthorizationUrl(
        Uri authorizationEndpoint,
        string clientId,
        string redirectUri,
        IReadOnlyList<string>? scopes,
        string state,
        string codeChallenge,
        Uri resourceUri)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", PkceHelper.Method),
            new("resource", resourceUri.ToString())
        };

        if (scopes is { Count: > 0 })
        {
            query.Add(new KeyValuePair<string, string>("scope", string.Join(' ', scopes)));
        }

        var builder = new UriBuilder(authorizationEndpoint);
        var existingQuery = builder.Query;
        var encoded = string.Join("&",
            query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        if (string.IsNullOrEmpty(existingQuery) || existingQuery == "?")
        {
            builder.Query = encoded;
        }
        else
        {
            builder.Query = existingQuery.TrimStart('?') + "&" + encoded;
        }

        return builder.Uri;
    }

    private async Task<TokenResponse> ExchangeCodeAsync(
        Uri tokenEndpoint,
        string clientId,
        string? clientSecret,
        string code,
        string redirectUri,
        string codeVerifier,
        Uri resourceUri,
        CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("client_id", clientId),
            new("code_verifier", codeVerifier),
            new("resource", resourceUri.ToString())
        };

        return await CallTokenEndpointAsync(tokenEndpoint, form, clientSecret, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> BuildRefreshForm(OAuthCacheEntry entry, Uri resourceUri)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", entry.RefreshToken!),
            new("client_id", entry.ClientId)
        };

        // Echo the resource indicator on refresh so issuers that scope tokens by audience preserve it.
        form.Add(new KeyValuePair<string, string>("resource", (entry.ResourceUri ?? resourceUri.ToString())));
        return form;
    }

    private async Task<TokenResponse> CallTokenEndpointAsync(
        Uri tokenEndpoint,
        IReadOnlyList<KeyValuePair<string, string>> form,
        string? clientSecret,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrEmpty(clientSecret))
        {
            // Confidential clients use HTTP Basic per RFC 6749 §2.3.1.
            var clientId = form.FirstOrDefault(pair => pair.Key == "client_id").Value;
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { /* ignored */ }
            throw new McpLenseAuthException(
                $"Token endpoint '{tokenEndpoint}' returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}".TrimEnd());
        }

        try
        {
            var parsed = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new McpLenseAuthException($"Token endpoint '{tokenEndpoint}' returned an empty body.");

            if (string.IsNullOrEmpty(parsed.AccessToken))
            {
                throw new McpLenseAuthException($"Token endpoint '{tokenEndpoint}' did not return an 'access_token'.");
            }

            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new McpLenseAuthException(
                $"Token endpoint '{tokenEndpoint}' did not return valid JSON: {ex.Message}", ex);
        }
    }

    private static OAuthCacheEntry BuildCacheEntry(
        ResolvedAuth auth,
        EndpointBundle endpoints,
        string redirectUri,
        string clientId,
        string? clientSecret,
        Uri resourceUri,
        TokenResponse tokens)
    {
        return new OAuthCacheEntry(
            ClientId: clientId,
            AccessToken: tokens.AccessToken!,
            TokenEndpoint: endpoints.TokenEndpoint,
            RedirectUri: redirectUri,
            Issuer: endpoints.Issuer,
            ClientSecret: clientSecret,
            RefreshToken: tokens.RefreshToken,
            ExpiresAt: ResolveExpiresAt(tokens),
            Scope: tokens.Scope ?? (auth.Scopes is { Count: > 0 } ? string.Join(' ', auth.Scopes) : null),
            ResourceUri: resourceUri.ToString(),
            RegistrationEndpoint: endpoints.RegistrationEndpoint);
    }

    private static DateTimeOffset? ResolveExpiresAt(TokenResponse tokens)
    {
        if (tokens.ExpiresIn is int seconds && seconds > 0)
        {
            return DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        return null;
    }

    private static string NewState()
    {
        var bytes = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return PkceHelper.Base64Url(bytes);
    }

    private sealed record EndpointBundle(string AuthorizationEndpoint, string TokenEndpoint, string? RegistrationEndpoint, string? Issuer);

    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }
}
