using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth.Discovery;

/// <summary>
/// Verifies <see cref="OAuthDiscoveryHandler"/>'s token-acquisition ladder, the 401-retry
/// behavior, request cloning, and the headless guard rail.
/// </summary>
public class OAuthDiscoveryHandlerTests
{
    // ---------- Fakes ---------------------------------------------------------------

    private sealed class InMemoryTokenCache : IOAuthTokenCache
    {
        public ConcurrentDictionary<string, OAuthCacheEntry> Store { get; } = new();
        public int LoadCount;
        public int SaveCount;
        public int DeleteCount;

        public Task<OAuthCacheEntry?> LoadAsync(string cacheKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref LoadCount);
            return Task.FromResult(Store.TryGetValue(cacheKey, out var e) ? e : null);
        }

        public Task SaveAsync(string cacheKey, OAuthCacheEntry entry, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SaveCount);
            Store[cacheKey] = entry;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string cacheKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DeleteCount);
            return Task.FromResult(Store.TryRemove(cacheKey, out _));
        }
    }

    /// <summary>Inner HTTP handler that the protected request flows into.</summary>
    private sealed class CapturingInnerHandler : HttpMessageHandler
    {
        public Queue<Func<HttpResponseMessage>> Responses { get; } = new();
        public List<string?> AuthHeaders { get; } = new();
        public List<byte[]> Bodies { get; } = new();
        public List<HttpMethod> Methods { get; } = new();
        public List<Uri?> Uris { get; } = new();
        public int Count => AuthHeaders.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            Methods.Add(request.Method);
            Uris.Add(request.RequestUri);
            Bodies.Add(request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));

            return Responses.Count > 0 ? Responses.Dequeue()() : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>HTTP transport for the orchestrator's discovery / token-endpoint traffic.</summary>
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

    private sealed class FakeBrowserLauncher : IBrowserLauncher
    {
        public bool Launched { get; private set; }
        public bool TryLaunch(Uri authorizationUrl)
        {
            Launched = true;
            return true;
        }
    }

    private sealed class FakeCallbackListener : IOAuthCallbackListener
    {
        public Uri RedirectUri { get; }
        public string Code { get; init; } = "interactive-code";
        public bool Disposed { get; private set; }

        public FakeCallbackListener(string preferred) =>
            RedirectUri = new Uri(preferred.Replace(":0/", ":54321/"));

        public Task<OAuthCallbackResult> WaitForCallbackAsync(string expectedState, CancellationToken cancellationToken)
            => Task.FromResult(new OAuthCallbackResult(Code, expectedState));

        public void Dispose() => Disposed = true;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    // ---------- Test helpers --------------------------------------------------------

    private sealed record HandlerSetup(
        OAuthDiscoveryHandler Handler,
        CapturingInnerHandler Inner,
        RoutingHandler DiscoveryRouter,
        InMemoryTokenCache Cache,
        Uri ResourceUri,
        string CacheKey,
        FakeBrowserLauncher Browser);

    private static HandlerSetup BuildHandler(
        ResolvedAuth auth,
        Uri resourceUri,
        bool? interactiveAllowed = null,
        TimeSpan? skew = null)
    {
        var inner = new CapturingInnerHandler();
        var router = new RoutingHandler();
        var cache = new InMemoryTokenCache();
        var browser = new FakeBrowserLauncher();
        var discoveryHttp = new HttpClient(router);

        var orchestrator = new OAuthFlowOrchestrator(
            discoveryHttp,
            cache,
            browser,
            listenerFactory: pref => new FakeCallbackListener(pref));

        var handler = new OAuthDiscoveryHandler(
            auth,
            resourceUri,
            cache,
            orchestrator,
            skew: skew,
            interactiveAllowed: interactiveAllowed,
            ownedResource: discoveryHttp)
        {
            InnerHandler = inner
        };

        var key = IOAuthTokenCache.ResolveCacheKey(auth.CacheName, resourceUri.ToString());
        return new HandlerSetup(handler, inner, router, cache, resourceUri, key, browser);
    }

    // ---------- Cache hit -----------------------------------------------------------

    [Fact]
    public async Task Send_CacheHit_ReusesTokenWithoutInvokingFlow()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth);
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri);

        setup.Cache.Store[setup.CacheKey] = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "cached-token",
            TokenEndpoint: "https://idp/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1));
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = new HttpClient(setup.Handler);
        var response = await client.GetAsync(new Uri("https://api.example.com/mcp"));

        response.IsSuccessStatusCode.ShouldBeTrue();
        setup.Inner.AuthHeaders.Single().ShouldBe("Bearer cached-token");
        setup.DiscoveryRouter.Calls.ShouldBeEmpty();
        setup.Browser.Launched.ShouldBeFalse();
    }

    // ---------- Refresh path --------------------------------------------------------

    [Fact]
    public async Task Send_CacheExpired_AttemptsRefreshBeforeInteractive()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth);
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri);

        setup.Cache.Store[setup.CacheKey] = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "old",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            RefreshToken: "rt",
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-30),
            ResourceUri: "https://api.example.com/mcp");

        setup.DiscoveryRouter.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "refreshed", "expires_in": 3600 }""");
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = new HttpClient(setup.Handler);
        await client.GetAsync(new Uri("https://api.example.com/mcp"));

        setup.Inner.AuthHeaders.Single().ShouldBe("Bearer refreshed");
        setup.Browser.Launched.ShouldBeFalse(); // refresh succeeded, no interactive flow
        setup.Cache.Store[setup.CacheKey].AccessToken.ShouldBe("refreshed");
    }

    // ---------- 401 reactive retry --------------------------------------------------

    [Fact]
    public async Task Send_401_ForcesRefreshAndRetriesOnce()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth);
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri);

        setup.Cache.Store[setup.CacheKey] = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "stale",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            RefreshToken: "rt",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1), // not expired by clock; server says it is
            ResourceUri: "https://api.example.com/mcp");

        setup.DiscoveryRouter.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "fresh", "expires_in": 3600 }""");
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = new HttpClient(setup.Handler);
        var response = await client.GetAsync(new Uri("https://api.example.com/mcp"));

        response.IsSuccessStatusCode.ShouldBeTrue();
        setup.Inner.Count.ShouldBe(2);
        setup.Inner.AuthHeaders[0].ShouldBe("Bearer stale");
        setup.Inner.AuthHeaders[1].ShouldBe("Bearer fresh");
    }

    [Fact]
    public async Task Send_401TwiceInARow_DoesNotLoopForever()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth);
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri);

        setup.Cache.Store[setup.CacheKey] = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "any",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            RefreshToken: "rt",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            ResourceUri: "https://api.example.com/mcp");

        setup.DiscoveryRouter.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "still-bad", "expires_in": 3600 }""");
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using var client = new HttpClient(setup.Handler);
        var response = await client.GetAsync(new Uri("https://api.example.com/mcp"));

        // Handler retries exactly once on 401. The second 401 propagates to the caller.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        setup.Inner.Count.ShouldBe(2);
    }

    // ---------- Body cloning --------------------------------------------------------

    [Fact]
    public async Task Send_PostWithBody_ClonesBodyOnRetry()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth);
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri);

        setup.Cache.Store[setup.CacheKey] = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "stale",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            RefreshToken: "rt",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            ResourceUri: "https://api.example.com/mcp");

        setup.DiscoveryRouter.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "fresh", "expires_in": 3600 }""");
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));

        var payload = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var client = new HttpClient(setup.Handler);
        var response = await client.PostAsync(new Uri("https://api.example.com/mcp"), content);

        response.IsSuccessStatusCode.ShouldBeTrue();
        setup.Inner.Count.ShouldBe(2);
        setup.Inner.Methods[0].ShouldBe(HttpMethod.Post);
        setup.Inner.Methods[1].ShouldBe(HttpMethod.Post);
        setup.Inner.Bodies[0].ShouldBe(payload);
        setup.Inner.Bodies[1].ShouldBe(payload);
    }

    // ---------- Interactive flow on cache miss -------------------------------------

    [Fact]
    public async Task Send_NoCachedEntry_RunsInteractiveFlow()
    {
        var auth = new ResolvedAuth(
            AuthKind.OAuth,
            ClientId: "static-cid",
            AuthorizationEndpoint: "https://idp.example.com/oauth/authorize",
            TokenEndpoint: "https://idp.example.com/oauth/token");
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri);

        setup.DiscoveryRouter.Routes["POST https://idp.example.com/oauth/token"] =
            _ => Json("""{ "access_token": "interactive", "expires_in": 3600 }""");
        setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = new HttpClient(setup.Handler);
        var response = await client.GetAsync(new Uri("https://api.example.com/mcp"));

        response.IsSuccessStatusCode.ShouldBeTrue();
        setup.Inner.AuthHeaders.Single().ShouldBe("Bearer interactive");
        setup.Browser.Launched.ShouldBeTrue();
        setup.Cache.Store[setup.CacheKey].AccessToken.ShouldBe("interactive");
    }

    // ---------- Headless guardrail --------------------------------------------------

    [Fact]
    public async Task Send_NoCachedEntry_InteractiveDisabled_Throws()
    {
        var auth = new ResolvedAuth(
            AuthKind.OAuth,
            ClientId: "cid",
            AuthorizationEndpoint: "https://idp.example.com/oauth/authorize",
            TokenEndpoint: "https://idp.example.com/oauth/token");
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri, interactiveAllowed: false);

        using var client = new HttpClient(setup.Handler);
        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            client.GetAsync(new Uri("https://api.example.com/mcp")));
        ex.Message.ShouldContain("--login");
        ex.Message.ShouldContain("MCPLENSE_NO_INTERACTIVE_FLOW");
        setup.Inner.Count.ShouldBe(0);
        setup.Browser.Launched.ShouldBeFalse();
    }

    [Fact]
    public async Task Send_RefreshFails_InteractiveDisabled_Throws()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth);
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri, interactiveAllowed: false);

        setup.Cache.Store[setup.CacheKey] = new OAuthCacheEntry(
            ClientId: "cid",
            AccessToken: "old",
            TokenEndpoint: "https://idp.example.com/oauth/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            RefreshToken: "rt",
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-30),
            ResourceUri: "https://api.example.com/mcp");

        // Refresh returns 400 → orchestrator returns null → handler must surface guardrail message.
        setup.DiscoveryRouter.Routes["POST https://idp.example.com/oauth/token"] =
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{ "error": "invalid_grant" }""", Encoding.UTF8, "application/json")
            };

        using var client = new HttpClient(setup.Handler);
        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            client.GetAsync(new Uri("https://api.example.com/mcp")));
        ex.Message.ShouldContain("--login");
    }

    // ---------- Concurrent acquisition ---------------------------------------------

    [Fact]
    public async Task Send_ConcurrentRequestsOnFirstAcquire_ShareSingleFlow()
    {
        var auth = new ResolvedAuth(
            AuthKind.OAuth,
            ClientId: "cid",
            AuthorizationEndpoint: "https://idp.example.com/oauth/authorize",
            TokenEndpoint: "https://idp.example.com/oauth/token");
        var resourceUri = new Uri("https://api.example.com/mcp");
        var setup = BuildHandler(auth, resourceUri);

        var tokenHits = 0;
        setup.DiscoveryRouter.Routes["POST https://idp.example.com/oauth/token"] = _ =>
        {
            Interlocked.Increment(ref tokenHits);
            return Json("""{ "access_token": "shared", "expires_in": 3600 }""");
        };

        // Each inner call returns 200 with "shared" bearer expected.
        for (var i = 0; i < 5; i++)
        {
            setup.Inner.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));
        }

        using var client = new HttpClient(setup.Handler);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => client.GetAsync(new Uri("https://api.example.com/mcp")))
            .ToArray();
        await Task.WhenAll(tasks);

        // Token endpoint hit exactly once even though 5 requests raced; cache gate serialised them.
        tokenHits.ShouldBe(1);
        setup.Inner.AuthHeaders.ShouldAllBe(h => h == "Bearer shared");
    }
}
