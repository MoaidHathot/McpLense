using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth.Discovery;

/// <summary>
/// Drives <see cref="OAuthFlowOrchestrator"/> with in-memory fakes for the browser, callback
/// listener, token cache, and HTTP transport. Each test asserts the order of HTTP calls and the
/// shape of the resulting cache entry.
/// </summary>
public class OAuthFlowOrchestratorTests
{
    // ---------- Fakes ---------------------------------------------------------------

    private sealed class FakeBrowserLauncher : IBrowserLauncher
    {
        public Uri? LaunchedUrl { get; private set; }
        public bool ReturnValue { get; init; } = true;

        public bool TryLaunch(Uri authorizationUrl)
        {
            LaunchedUrl = authorizationUrl;
            return ReturnValue;
        }
    }

    private sealed class FakeCallbackListener : IOAuthCallbackListener
    {
        public Uri RedirectUri { get; }
        public string Code { get; init; } = "auth-code";
        public string? CapturedExpectedState { get; private set; }
        public bool Disposed { get; private set; }

        public FakeCallbackListener(string preferred)
        {
            RedirectUri = new Uri(preferred.Replace(":0/", ":54321/"));
        }

        public Task<OAuthCallbackResult> WaitForCallbackAsync(string expectedState, CancellationToken cancellationToken)
        {
            CapturedExpectedState = expectedState;
            return Task.FromResult(new OAuthCallbackResult(Code, expectedState));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class InMemoryTokenCache : IOAuthTokenCache
    {
        public ConcurrentDictionary<string, OAuthCacheEntry> Store { get; } = new();
        public List<string> Saves { get; } = new();
        public List<string> Deletes { get; } = new();

        public Task<OAuthCacheEntry?> LoadAsync(string cacheKey, CancellationToken cancellationToken)
            => Task.FromResult(Store.TryGetValue(cacheKey, out var e) ? e : null);

        public Task SaveAsync(string cacheKey, OAuthCacheEntry entry, CancellationToken cancellationToken)
        {
            Store[cacheKey] = entry;
            Saves.Add(cacheKey);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string cacheKey, CancellationToken cancellationToken)
        {
            Deletes.Add(cacheKey);
            return Task.FromResult(Store.TryRemove(cacheKey, out _));
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();
        public List<(HttpMethod Method, Uri Uri, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!, body));

            var key = $"{request.Method} {request.RequestUri}";
            if (Routes.TryGetValue(key, out var factory))
            {
                return factory(request);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No route for {key}")
            };
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static IReadOnlyDictionary<string, string> ParseForm(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
            {
                continue;
            }
            dict[Uri.UnescapeDataString(pair[..idx])] = Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        return dict;
    }

    // ---------- Tests ---------------------------------------------------------------

    [Fact]
    public async Task RunInteractive_DiscoveryPath_DiscoversRegistersAndExchangesCode()
    {
        var router = new RoutingHandler();
        var resourceUri = new Uri("https://api.example.com/mcp");

        // PRM advertises one authorization server.
        router.Routes["GET https://api.example.com/.well-known/oauth-protected-resource/mcp"] =
            _ => Json("""{ "authorization_servers": ["https://idp.example.com"] }""");

        // ASM advertises endpoints + DCR.
        router.Routes["GET https://idp.example.com/.well-known/oauth-authorization-server"] =
            _ => Json("""{ "issuer": "https://idp.example.com", "authorization_endpoint": "https://idp.example.com/oauth/authorize", "token_endpoint": "https://idp.example.com/oauth/token", "registration_endpoint": "https://idp.example.com/oauth/register" }""");

        router.Routes["POST https://idp.example.com/oauth/register"] =
            _ => Json("""{ "client_id": "dcr-client-1" }""", HttpStatusCode.Created);

        router.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "tok-1", "expires_in": 3600, "refresh_token": "rt-1", "token_type": "Bearer", "scope": "mcp.read" }""");

        using var http = new HttpClient(router);
        var cache = new InMemoryTokenCache();
        var browser = new FakeBrowserLauncher();
        FakeCallbackListener? listener = null;

        var orchestrator = new OAuthFlowOrchestrator(
            http,
            cache,
            browser,
            listenerFactory: preferred =>
            {
                listener = new FakeCallbackListener(preferred);
                return listener;
            });

        var auth = new ResolvedAuth(AuthKind.OAuth, Scopes: new[] { "mcp.read" });

        var entry = await orchestrator.RunInteractiveAsync(auth, resourceUri, CancellationToken.None);

        // Cache write occurred under the resource-derived key.
        var expectedKey = IOAuthTokenCache.ResolveCacheKey(null, resourceUri.ToString());
        cache.Saves.Single().ShouldBe(expectedKey);
        cache.Store[expectedKey].AccessToken.ShouldBe("tok-1");

        // Returned entry shape.
        entry.ClientId.ShouldBe("dcr-client-1");
        entry.AccessToken.ShouldBe("tok-1");
        entry.RefreshToken.ShouldBe("rt-1");
        entry.Issuer.ShouldBe("https://idp.example.com");
        entry.TokenEndpoint.ShouldBe("https://idp.example.com/oauth/token");
        entry.ResourceUri.ShouldBe("https://api.example.com/mcp");
        entry.Scope.ShouldBe("mcp.read");
        entry.ExpiresAt.ShouldNotBeNull();

        // Browser was launched at the authorization endpoint with PKCE + state + resource.
        browser.LaunchedUrl.ShouldNotBeNull();
        browser.LaunchedUrl!.GetLeftPart(UriPartial.Path).ShouldBe("https://idp.example.com/oauth/authorize");
        var query = System.Web.HttpUtility.ParseQueryString(browser.LaunchedUrl.Query);
        query["response_type"].ShouldBe("code");
        query["client_id"].ShouldBe("dcr-client-1");
        query["code_challenge_method"].ShouldBe("S256");
        query["code_challenge"].ShouldNotBeNullOrEmpty();
        query["state"].ShouldNotBeNullOrEmpty();
        query["resource"].ShouldBe("https://api.example.com/mcp");
        query["scope"].ShouldBe("mcp.read");

        // Listener was invoked with the same state we sent in the URL.
        listener.ShouldNotBeNull();
        listener!.CapturedExpectedState.ShouldBe(query["state"]);
        listener.Disposed.ShouldBeTrue();

        // Token endpoint received PKCE + resource.
        var tokenCall = router.Calls.Single(c => c.Uri.AbsoluteUri == "https://idp.example.com/oauth/token");
        var form = ParseForm(tokenCall.Body);
        form["grant_type"].ShouldBe("authorization_code");
        form["code"].ShouldBe(listener.Code);
        form["client_id"].ShouldBe("dcr-client-1");
        form["code_verifier"].ShouldNotBeNullOrEmpty();
        form["resource"].ShouldBe("https://api.example.com/mcp");
        form["redirect_uri"].ShouldBe(listener.RedirectUri.ToString());
    }

    [Fact]
    public async Task RunInteractive_StaticEndpointsAndClientId_BypassDiscoveryAndDcr()
    {
        var router = new RoutingHandler();
        router.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "tok-static", "expires_in": 600 }""");

        using var http = new HttpClient(router);
        var cache = new InMemoryTokenCache();
        var browser = new FakeBrowserLauncher();
        FakeCallbackListener? listener = null;

        var orchestrator = new OAuthFlowOrchestrator(
            http, cache, browser,
            listenerFactory: pref => listener = new FakeCallbackListener(pref));

        var auth = new ResolvedAuth(
            AuthKind.OAuth,
            ClientId: "static-client",
            ClientSecret: "secret",
            AuthorizationEndpoint: "https://idp.example.com/oauth/authorize",
            TokenEndpoint: "https://idp.example.com/oauth/token");

        var entry = await orchestrator.RunInteractiveAsync(auth, new Uri("https://api.example.com/mcp"), CancellationToken.None);

        // No PRM, ASM, or registration calls — only the token POST.
        router.Calls.Count.ShouldBe(1);
        router.Calls.Single().Uri.AbsoluteUri.ShouldBe("https://idp.example.com/oauth/token");

        entry.ClientId.ShouldBe("static-client");
        entry.ClientSecret.ShouldBe("secret");
        entry.AccessToken.ShouldBe("tok-static");
        entry.Issuer.ShouldBeNull(); // no discovery → no issuer

        // Confidential client: token POST included Authorization: Basic.
        var lastTokenRequest = router.Calls[0];
        // Re-issue routing so we can inspect headers — RoutingHandler captures only URI/body.
        // Instead verify body has client_id, then assert via a follow-up route.
        var form = ParseForm(lastTokenRequest.Body);
        form["client_id"].ShouldBe("static-client");
        form["grant_type"].ShouldBe("authorization_code");

        listener.ShouldNotBeNull();
        listener!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task RunInteractive_ConfidentialClient_SendsBasicAuthHeader()
    {
        // Capture the token request to verify the Authorization header (RFC 6749 §2.3.1).
        AuthenticationHeaderValueCapture? capturedAuth = null;
        var router = new RoutingHandler();
        router.Routes["POST https://idp.example.com/oauth/token"] = req =>
        {
            capturedAuth = new AuthenticationHeaderValueCapture(
                req.Headers.Authorization?.Scheme,
                req.Headers.Authorization?.Parameter);
            return Json("""{ "access_token": "tok", "expires_in": 60 }""");
        };

        using var http = new HttpClient(router);
        var orchestrator = new OAuthFlowOrchestrator(
            http,
            new InMemoryTokenCache(),
            new FakeBrowserLauncher(),
            listenerFactory: pref => new FakeCallbackListener(pref));

        var auth = new ResolvedAuth(
            AuthKind.OAuth,
            ClientId: "cid",
            ClientSecret: "shh",
            AuthorizationEndpoint: "https://idp.example.com/oauth/authorize",
            TokenEndpoint: "https://idp.example.com/oauth/token");

        await orchestrator.RunInteractiveAsync(auth, new Uri("https://api.example.com/mcp"), CancellationToken.None);

        capturedAuth.ShouldNotBeNull();
        capturedAuth!.Scheme.ShouldBe("Basic");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(capturedAuth.Parameter!));
        decoded.ShouldBe("cid:shh");
    }

    private sealed record AuthenticationHeaderValueCapture(string? Scheme, string? Parameter);

    [Fact]
    public async Task RunInteractive_CachedClientId_ReusedInsteadOfReregistering()
    {
        var router = new RoutingHandler();
        router.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "tok", "expires_in": 60 }""");
        // Note: no /oauth/register route — DCR must NOT be invoked.

        var resourceUri = new Uri("https://api.example.com/mcp");
        var cache = new InMemoryTokenCache();
        var key = IOAuthTokenCache.ResolveCacheKey(null, resourceUri.ToString());
        cache.Store[key] = new OAuthCacheEntry(
            ClientId: "previously-registered",
            AccessToken: "stale",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback");

        using var http = new HttpClient(router);
        var orchestrator = new OAuthFlowOrchestrator(
            http, cache, new FakeBrowserLauncher(),
            listenerFactory: pref => new FakeCallbackListener(pref));

        var auth = new ResolvedAuth(
            AuthKind.OAuth,
            AuthorizationEndpoint: "https://idp.example.com/oauth/authorize",
            TokenEndpoint: "https://idp.example.com/oauth/token");

        var entry = await orchestrator.RunInteractiveAsync(auth, resourceUri, CancellationToken.None);

        entry.ClientId.ShouldBe("previously-registered");
        router.Calls.ShouldNotContain(c => c.Uri.AbsoluteUri.EndsWith("/register"));
    }

    [Fact]
    public async Task RunInteractive_BrowserLaunchFails_PrintsAuthorizationUrlToStderr()
    {
        var router = new RoutingHandler();
        router.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "tok", "expires_in": 60 }""");

        using var http = new HttpClient(router);
        var stderr = new StringWriter();
        var browser = new FakeBrowserLauncher { ReturnValue = false };

        var orchestrator = new OAuthFlowOrchestrator(
            http,
            new InMemoryTokenCache(),
            browser,
            listenerFactory: pref => new FakeCallbackListener(pref),
            stderr: stderr);

        var auth = new ResolvedAuth(
            AuthKind.OAuth,
            ClientId: "cid",
            AuthorizationEndpoint: "https://idp.example.com/oauth/authorize",
            TokenEndpoint: "https://idp.example.com/oauth/token");

        await orchestrator.RunInteractiveAsync(auth, new Uri("https://api.example.com/mcp"), CancellationToken.None);

        var output = stderr.ToString();
        output.ShouldContain("Open this URL in a browser");
        output.ShouldContain("https://idp.example.com/oauth/authorize");
    }

    [Fact]
    public async Task RunInteractive_NoIssuerAndNoStaticEndpoints_Throws()
    {
        var router = new RoutingHandler();
        // PRM returns 404 → null → no authorization servers discoverable.
        router.Routes["GET https://api.example.com/.well-known/oauth-protected-resource/mcp"] =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        using var http = new HttpClient(router);
        var orchestrator = new OAuthFlowOrchestrator(
            http, new InMemoryTokenCache(), new FakeBrowserLauncher(),
            listenerFactory: pref => new FakeCallbackListener(pref));

        var auth = new ResolvedAuth(AuthKind.OAuth);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            orchestrator.RunInteractiveAsync(auth, new Uri("https://api.example.com/mcp"), CancellationToken.None));
        ex.Message.ShouldContain("issuer");
    }

    [Fact]
    public async Task RunInteractive_NoRegistrationEndpointAndNoStaticClientId_Throws()
    {
        var router = new RoutingHandler();
        router.Routes["GET https://api.example.com/.well-known/oauth-protected-resource/mcp"] =
            _ => Json("""{ "authorization_servers": ["https://idp.example.com"] }""");
        router.Routes["GET https://idp.example.com/.well-known/oauth-authorization-server"] =
            _ => Json("""{ "issuer": "https://idp.example.com", "authorization_endpoint": "https://idp.example.com/a", "token_endpoint": "https://idp.example.com/t" }""");

        using var http = new HttpClient(router);
        var orchestrator = new OAuthFlowOrchestrator(
            http, new InMemoryTokenCache(), new FakeBrowserLauncher(),
            listenerFactory: pref => new FakeCallbackListener(pref));

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            orchestrator.RunInteractiveAsync(new ResolvedAuth(AuthKind.OAuth), new Uri("https://api.example.com/mcp"), CancellationToken.None));
        ex.Message.ShouldContain("registration_endpoint");
        ex.Message.ShouldContain("clientId");
    }

    // ---------- TryRefresh ---------------------------------------------------------

    [Fact]
    public async Task TryRefresh_HappyPath_UpdatesEntryAndSavesCache()
    {
        var router = new RoutingHandler();
        router.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "new-access", "expires_in": 1800, "refresh_token": "new-refresh", "scope": "mcp.read mcp.write" }""");

        using var http = new HttpClient(router);
        var cache = new InMemoryTokenCache();
        var orchestrator = new OAuthFlowOrchestrator(
            http, cache, new FakeBrowserLauncher(),
            listenerFactory: pref => new FakeCallbackListener(pref));

        var resourceUri = new Uri("https://api.example.com/mcp");
        var auth = new ResolvedAuth(AuthKind.OAuth);
        var existing = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "old-access",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            RefreshToken: "old-refresh",
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-10),
            Scope: "mcp.read",
            ResourceUri: "https://api.example.com/mcp");

        var refreshed = await orchestrator.TryRefreshAsync(existing, auth, resourceUri, CancellationToken.None);

        refreshed.ShouldNotBeNull();
        refreshed!.AccessToken.ShouldBe("new-access");
        refreshed.RefreshToken.ShouldBe("new-refresh");
        refreshed.Scope.ShouldBe("mcp.read mcp.write");
        refreshed.ExpiresAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(25));

        // Saved under the same key the entry was originally cached under.
        var key = IOAuthTokenCache.ResolveCacheKey(null, resourceUri.ToString());
        cache.Store[key].AccessToken.ShouldBe("new-access");

        // Form sent to token endpoint contains refresh_token grant + resource.
        var form = ParseForm(router.Calls.Single().Body);
        form["grant_type"].ShouldBe("refresh_token");
        form["refresh_token"].ShouldBe("old-refresh");
        form["client_id"].ShouldBe("cid");
        form["resource"].ShouldBe("https://api.example.com/mcp");
    }

    [Fact]
    public async Task TryRefresh_NoRefreshTokenOnEntry_ReturnsNullWithoutHttp()
    {
        var router = new RoutingHandler();
        using var http = new HttpClient(router);
        var orchestrator = new OAuthFlowOrchestrator(
            http, new InMemoryTokenCache(), new FakeBrowserLauncher(),
            listenerFactory: pref => new FakeCallbackListener(pref));

        var existing = new OAuthCacheEntry("cid", "tok", "https://idp/t", "http://cb"); // no refresh token

        var result = await orchestrator.TryRefreshAsync(
            existing,
            new ResolvedAuth(AuthKind.OAuth),
            new Uri("https://api.example.com/mcp"),
            CancellationToken.None);

        result.ShouldBeNull();
        router.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryRefresh_TokenEndpointError_ReturnsNull()
    {
        var router = new RoutingHandler();
        router.Routes["POST https://idp.example.com/oauth/token"] =
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{ "error": "invalid_grant" }""", Encoding.UTF8, "application/json")
            };

        using var http = new HttpClient(router);
        var orchestrator = new OAuthFlowOrchestrator(
            http, new InMemoryTokenCache(), new FakeBrowserLauncher(),
            listenerFactory: pref => new FakeCallbackListener(pref));

        var existing = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "tok",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            RefreshToken: "rt",
            ResourceUri: "https://api.example.com/mcp");

        var result = await orchestrator.TryRefreshAsync(
            existing,
            new ResolvedAuth(AuthKind.OAuth),
            new Uri("https://api.example.com/mcp"),
            CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task TryRefresh_RefreshResponseOmitsRefreshToken_PreservesExisting()
    {
        var router = new RoutingHandler();
        router.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "rotated", "expires_in": 600 }""");

        using var http = new HttpClient(router);
        var orchestrator = new OAuthFlowOrchestrator(
            http, new InMemoryTokenCache(), new FakeBrowserLauncher(),
            listenerFactory: pref => new FakeCallbackListener(pref));

        var existing = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "old",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://cb",
            RefreshToken: "preserved-rt",
            Scope: "mcp.read",
            ResourceUri: "https://api.example.com/mcp");

        var refreshed = await orchestrator.TryRefreshAsync(
            existing,
            new ResolvedAuth(AuthKind.OAuth),
            new Uri("https://api.example.com/mcp"),
            CancellationToken.None);

        refreshed.ShouldNotBeNull();
        refreshed!.AccessToken.ShouldBe("rotated");
        refreshed.RefreshToken.ShouldBe("preserved-rt");
        refreshed.Scope.ShouldBe("mcp.read");
    }
}
