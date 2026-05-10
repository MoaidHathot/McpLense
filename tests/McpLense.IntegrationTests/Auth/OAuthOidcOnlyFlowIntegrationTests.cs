using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests.Auth;

/// <summary>
/// End-to-end tests against an in-process MCP server hosting an OIDC-only mock identity provider
/// (Microsoft Entra ID v2.0 shape). Verifies that <see cref="OAuthDiscoveryClient"/>'s three-form
/// fallback ladder discovers the metadata at <c>/.well-known/openid-configuration</c> and that the
/// full OAuth flow then completes through <see cref="OAuthFlowOrchestrator"/>.
/// </summary>
[Collection("OAuthOidcOnlyHttpTestServer")]
public class OAuthOidcOnlyFlowIntegrationTests
{
    private readonly OAuthOidcOnlyHttpTestServerFixture _fixture;

    public OAuthOidcOnlyFlowIntegrationTests(OAuthOidcOnlyHttpTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Reuses the live-authorize simulator pattern from <see cref="OAuthFlowIntegrationTests"/>:
    /// follows the authorize redirect in-process and surrenders the code+state to the orchestrator.
    /// </summary>
    private sealed class LiveAuthorizeSimulator : IBrowserLauncher, IDisposable
    {
        private readonly HttpClient _http;
        private Uri? _launchedUrl;
        public Uri RedirectUri { get; }
        public bool Launched => _launchedUrl is not null;

        public LiveAuthorizeSimulator(string preferredRedirectUri)
        {
            RedirectUri = new Uri(preferredRedirectUri.Replace(":0/", ":54322/"));
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
                RedirectUri = new Uri(preferred.Replace(":0/", ":54322/"));
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

    [Fact]
    public async Task RfcAsmEndpoint_Returns404_OidcEndpointReturnsMetadata()
    {
        // Sanity-check that the OIDC-only fixture is wired the way we think it is. This guards
        // against silent fixture drift breaking the more important orchestration test below.
        using var http = new HttpClient();

        using var asmResponse = await http.GetAsync(_fixture.AsmUrl);
        asmResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var oidcResponse = await http.GetAsync(_fixture.OidcUrl);
        oidcResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await oidcResponse.Content.ReadAsStringAsync();
        body.ShouldContain("authorization_endpoint");
        body.ShouldContain("token_endpoint");
    }

    [Fact]
    public async Task Discovery_FallsBackToOidc_AndReturnsExpectedEndpoints()
    {
        using var http = new HttpClient();
        var client = new OAuthDiscoveryClient(http);

        var asm = await client.FetchAuthorizationServerMetadataAsync(
            new Uri(_fixture.BaseUrl.TrimEnd('/')),
            CancellationToken.None);

        asm.AuthorizationEndpoint.ShouldBe(_fixture.AuthorizeUrl);
        asm.TokenEndpoint.ShouldBe(_fixture.TokenUrl);
        asm.RegistrationEndpoint.ShouldBe(_fixture.RegisterUrl);
    }

    [Fact]
    public async Task RunInteractive_DriversFullFlowOverOidcDiscovery_AcquiresToken()
    {
        var cache = new InMemoryTokenCache();
        var simulator = new LiveAuthorizeSimulator("http://127.0.0.1:0/callback");
        using var discoveryHttp = new HttpClient(new SocketsHttpHandler(), disposeHandler: true);
        var orchestrator = new OAuthFlowOrchestrator(
            discoveryHttp,
            cache,
            simulator,
            listenerFactory: pref => simulator.BuildListener(pref));
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
            // DCR fired against the registration_endpoint advertised in the OIDC document.
            entry.ClientId.ShouldStartWith("dcr-");
        }
        finally
        {
            simulator.Dispose();
        }
    }
}
