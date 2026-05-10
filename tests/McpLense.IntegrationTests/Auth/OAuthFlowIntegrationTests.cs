using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests.Auth;

/// <summary>
/// End-to-end tests against a real in-process MCP server hosting a mock OAuth Identity Provider.
///
/// These tests exercise:
/// <list type="bullet">
///   <item>Live PRM/ASM/DCR/authorize/token endpoints (HTTP).</item>
///   <item><see cref="OAuthFlowOrchestrator"/> driving the full flow with a fake browser/listener
///   that intercepts the authorize redirect in-process.</item>
///   <item><see cref="OAuthDiscoveryHandler"/> attaching the resulting bearer to MCP requests
///   that flow through the gated server.</item>
/// </list>
/// </summary>
[Collection("OAuthHttpTestServer")]
public class OAuthFlowIntegrationTests
{
    private readonly OAuthHttpTestServerFixture _fixture;

    public OAuthFlowIntegrationTests(OAuthHttpTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    // ---------- Fakes shared by tests ---------------------------------------------

    /// <summary>
    /// Fake browser+listener pair that completes the authorize hop in-process: when the orchestrator
    /// "launches" the auth URL and waits for a callback, this simulator GETs the URL with redirects
    /// disabled and pulls the code out of the <c>Location</c> header.
    /// </summary>
    private sealed class LiveAuthorizeSimulator : IBrowserLauncher, IDisposable
    {
        private readonly HttpClient _http;
        private Uri? _launchedUrl;
        private readonly string _preferredRedirect;
        public Uri RedirectUri { get; }
        public bool Launched => _launchedUrl is not null;

        public LiveAuthorizeSimulator(string preferredRedirectUri)
        {
            _preferredRedirect = preferredRedirectUri;
            RedirectUri = new Uri(preferredRedirectUri.Replace(":0/", ":54321/"));
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        }

        public bool TryLaunch(Uri authorizationUrl)
        {
            _launchedUrl = authorizationUrl;
            return true;
        }

        public IOAuthCallbackListener BuildListener(string preferred)
            => new ListenerImpl(this, preferred);

        public void Dispose() => _http.Dispose();

        private sealed class ListenerImpl : IOAuthCallbackListener
        {
            private readonly LiveAuthorizeSimulator _outer;
            public Uri RedirectUri { get; }

            public ListenerImpl(LiveAuthorizeSimulator outer, string preferred)
            {
                _outer = outer;
                RedirectUri = new Uri(preferred.Replace(":0/", ":54321/"));
            }

            public async Task<OAuthCallbackResult> WaitForCallbackAsync(string expectedState, CancellationToken cancellationToken)
            {
                if (_outer._launchedUrl is null)
                {
                    throw new InvalidOperationException("Browser launcher was not invoked before listener.");
                }

                using var response = await _outer._http.GetAsync(_outer._launchedUrl, cancellationToken);
                if ((int)response.StatusCode is < 300 or >= 400)
                {
                    throw new InvalidOperationException(
                        $"Expected redirect from authorize endpoint but got HTTP {(int)response.StatusCode}.");
                }

                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("Authorize endpoint did not return a Location header.");
                var query = System.Web.HttpUtility.ParseQueryString(location.Query);
                var code = query["code"] ?? throw new InvalidOperationException("Missing 'code' in authorize redirect.");
                var state = query["state"] ?? throw new InvalidOperationException("Missing 'state' in authorize redirect.");

                if (!string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Authorize state '{state}' did not match orchestrator state '{expectedState}'.");
                }

                return new OAuthCallbackResult(code, state);
            }

            public void Dispose() { }
        }
    }

    private sealed class InMemoryTokenCache : IOAuthTokenCache
    {
        public Dictionary<string, OAuthCacheEntry> Store { get; } = new();

        public Task<OAuthCacheEntry?> LoadAsync(string cacheKey, CancellationToken cancellationToken)
            => Task.FromResult(Store.TryGetValue(cacheKey, out var e) ? e : null);

        public Task SaveAsync(string cacheKey, OAuthCacheEntry entry, CancellationToken cancellationToken)
        {
            Store[cacheKey] = entry;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string cacheKey, CancellationToken cancellationToken)
            => Task.FromResult(Store.Remove(cacheKey));
    }

    private static OAuthFlowOrchestrator BuildOrchestrator(
        InMemoryTokenCache cache,
        out LiveAuthorizeSimulator simulator,
        out HttpClient discoveryHttp)
    {
        var sim = new LiveAuthorizeSimulator("http://127.0.0.1:0/callback");
        var http = new HttpClient(new SocketsHttpHandler(), disposeHandler: true);
        simulator = sim;
        discoveryHttp = http;
        return new OAuthFlowOrchestrator(http, cache, sim, listenerFactory: pref => sim.BuildListener(pref));
    }

    // ---------- Discovery ---------------------------------------------------------

    [Fact]
    public async Task Discovery_AgainstLiveIdp_ReturnsExpectedEndpoints()
    {
        using var http = new HttpClient();
        var client = new OAuthDiscoveryClient(http);

        var prm = await client.FetchProtectedResourceMetadataAsync(new Uri(_fixture.PrmUrl), CancellationToken.None);
        prm.ShouldNotBeNull();
        prm!.AuthorizationServers.ShouldNotBeNull();
        prm.AuthorizationServers!.Single().ShouldBe(_fixture.BaseUrl.TrimEnd('/'));

        var asm = await client.FetchAuthorizationServerMetadataAsync(new Uri(_fixture.BaseUrl.TrimEnd('/')), CancellationToken.None);
        asm.AuthorizationEndpoint.ShouldBe(_fixture.AuthorizeUrl);
        asm.TokenEndpoint.ShouldBe(_fixture.TokenUrl);
        asm.RegistrationEndpoint.ShouldBe(_fixture.RegisterUrl);
    }

    [Fact]
    public async Task Mcp_NoAuthorization_Returns401WithPrmAdvertised()
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(_fixture.BaseUrl);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        var wwwAuth = response.Headers.WwwAuthenticate.Single();
        wwwAuth.Scheme.ShouldBe("Bearer");
        wwwAuth.Parameter.ShouldNotBeNull().ShouldContain("oauth-protected-resource");
    }

    [Fact]
    public async Task Mcp_BogusBearerToken_Returns401()
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, _fixture.BaseUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-token");
        using var response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
    }

    // ---------- Full flow ---------------------------------------------------------

    [Fact]
    public async Task RunInteractive_DriversFullFlow_AcquiresTokenAndCachesIt()
    {
        var cache = new InMemoryTokenCache();
        var orchestrator = BuildOrchestrator(cache, out var simulator, out var discoveryHttp);
        try
        {
            var resourceUri = new Uri(_fixture.BaseUrl);
            var auth = new ResolvedAuth(AuthKind.OAuth, Scopes: new[] { "mcp.read" });

            var entry = await orchestrator.RunInteractiveAsync(auth, resourceUri, CancellationToken.None);

            simulator.Launched.ShouldBeTrue();
            entry.AccessToken.ShouldNotBeNullOrEmpty();
            entry.RefreshToken.ShouldNotBeNullOrEmpty();
            entry.TokenEndpoint.ShouldBe(_fixture.TokenUrl);
            entry.Issuer.ShouldBe(_fixture.BaseUrl.TrimEnd('/'));
            entry.ClientId.ShouldStartWith("dcr-");

            var key = IOAuthTokenCache.ResolveCacheKey(null, resourceUri.ToString());
            cache.Store[key].AccessToken.ShouldBe(entry.AccessToken);
        }
        finally
        {
            simulator.Dispose();
            discoveryHttp.Dispose();
        }
    }

    [Fact]
    public async Task RunInteractive_StaticClientId_BypassesDcrButCompletesFlow()
    {
        var cache = new InMemoryTokenCache();
        var orchestrator = BuildOrchestrator(cache, out var simulator, out var discoveryHttp);
        try
        {
            var auth = new ResolvedAuth(
                AuthKind.OAuth,
                ClientId: "preconfigured-client",
                AuthorizationEndpoint: _fixture.AuthorizeUrl,
                TokenEndpoint: _fixture.TokenUrl);

            var entry = await orchestrator.RunInteractiveAsync(auth, new Uri(_fixture.BaseUrl), CancellationToken.None);

            entry.ClientId.ShouldBe("preconfigured-client");
            entry.AccessToken.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            simulator.Dispose();
            discoveryHttp.Dispose();
        }
    }

    // ---------- Refresh -----------------------------------------------------------

    [Fact]
    public async Task TryRefresh_AgainstLiveIdp_RotatesAccessToken()
    {
        var cache = new InMemoryTokenCache();
        var orchestrator = BuildOrchestrator(cache, out var simulator, out var discoveryHttp);
        try
        {
            var resourceUri = new Uri(_fixture.BaseUrl);
            var auth = new ResolvedAuth(AuthKind.OAuth);

            var initial = await orchestrator.RunInteractiveAsync(auth, resourceUri, CancellationToken.None);
            var refreshed = await orchestrator.TryRefreshAsync(initial, auth, resourceUri, CancellationToken.None);

            refreshed.ShouldNotBeNull();
            refreshed!.AccessToken.ShouldNotBe(initial.AccessToken);
            refreshed.ClientId.ShouldBe(initial.ClientId);
        }
        finally
        {
            simulator.Dispose();
            discoveryHttp.Dispose();
        }
    }

    // ---------- OAuthDiscoveryHandler against the gated MCP -----------------------

    [Fact]
    public async Task OAuthDiscoveryHandler_CachedToken_TalksToProtectedMcp()
    {
        var cache = new InMemoryTokenCache();

        // Acquire a real token via the orchestrator first; this also writes to the cache.
        var orchestrator = BuildOrchestrator(cache, out var simulator, out var discoveryHttp);
        var resourceUri = new Uri(_fixture.BaseUrl);
        var auth = new ResolvedAuth(AuthKind.OAuth);

        OAuthCacheEntry initial;
        try
        {
            initial = await orchestrator.RunInteractiveAsync(auth, resourceUri, CancellationToken.None);
        }
        finally
        {
            simulator.Dispose();
            discoveryHttp.Dispose();
        }

        // Now build a handler that should reuse the cached token (no further interactive flow).
        using var orchestratorHttp = new HttpClient(new SocketsHttpHandler(), disposeHandler: true);
        var nonInteractiveOrch = new OAuthFlowOrchestrator(
            orchestratorHttp,
            cache,
            new ThrowingBrowserLauncher(), // must not be called
            listenerFactory: _ => throw new InvalidOperationException("Interactive listener must not be created."));

        using var handler = new OAuthDiscoveryHandler(auth, resourceUri, cache, nonInteractiveOrch)
        {
            InnerHandler = new SocketsHttpHandler()
        };
        using var client = new HttpClient(handler);

        // Simple GET to the MCP base URL: with cached bearer it should not be 401 (the MCP route
        // returns whatever the SDK responds with; we just need to confirm we made it past auth).
        using var response = await client.GetAsync(_fixture.BaseUrl);
        response.StatusCode.ShouldNotBe(System.Net.HttpStatusCode.Unauthorized);

        // Cache still holds the same token (no refresh was needed).
        var key = IOAuthTokenCache.ResolveCacheKey(null, resourceUri.ToString());
        cache.Store[key].AccessToken.ShouldBe(initial.AccessToken);
    }

    [Fact]
    public async Task OAuthDiscoveryHandler_NoCacheNoInteractive_ThrowsHelpfulError()
    {
        var cache = new InMemoryTokenCache();
        var resourceUri = new Uri(_fixture.BaseUrl);
        var auth = new ResolvedAuth(AuthKind.OAuth);

        using var orchestratorHttp = new HttpClient(new SocketsHttpHandler(), disposeHandler: true);
        var orch = new OAuthFlowOrchestrator(
            orchestratorHttp, cache, new ThrowingBrowserLauncher(),
            listenerFactory: _ => throw new InvalidOperationException("Should not be created."));

        using var handler = new OAuthDiscoveryHandler(
            auth, resourceUri, cache, orch, interactiveAllowed: false)
        {
            InnerHandler = new SocketsHttpHandler()
        };
        using var client = new HttpClient(handler);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() => client.GetAsync(_fixture.BaseUrl));
        ex.Message.ShouldContain("--login");
    }

    private sealed class ThrowingBrowserLauncher : IBrowserLauncher
    {
        public bool TryLaunch(Uri authorizationUrl)
            => throw new InvalidOperationException("Browser launch must not be attempted in this test.");
    }
}
