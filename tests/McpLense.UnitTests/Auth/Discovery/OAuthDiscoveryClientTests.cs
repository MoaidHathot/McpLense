using System.Net;
using System.Net.Http;
using System.Text;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth.Discovery;

public class OAuthDiscoveryClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_factory(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    [Theory]
    [InlineData("https://example.com/", "https://example.com/.well-known/oauth-protected-resource")]
    [InlineData("https://example.com", "https://example.com/.well-known/oauth-protected-resource")]
    [InlineData("https://example.com/mcp", "https://example.com/.well-known/oauth-protected-resource/mcp")]
    [InlineData("https://example.com:8443/api/mcp", "https://example.com:8443/.well-known/oauth-protected-resource/api/mcp")]
    public void BuildPrmUri_PutsWellKnownBeforePath(string resource, string expected)
    {
        var uri = OAuthDiscoveryClient.BuildPrmUri(new Uri(resource));

        uri.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://idp.example.com/", "https://idp.example.com/.well-known/oauth-authorization-server")]
    [InlineData("https://idp.example.com/tenant1", "https://idp.example.com/.well-known/oauth-authorization-server/tenant1")]
    public void BuildAsmUri_FollowsRfc8414(string issuer, string expected)
    {
        var uri = OAuthDiscoveryClient.BuildAsmUri(new Uri(issuer));

        uri.ToString().ShouldBe(expected);
    }

    [Fact]
    public async Task FetchProtectedResourceMetadata_404_ReturnsNull()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(stub);
        var client = new OAuthDiscoveryClient(http);

        var result = await client.FetchProtectedResourceMetadataAsync(
            new Uri("https://example.com/.well-known/oauth-protected-resource"),
            CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task FetchProtectedResourceMetadata_200_ParsesAuthorizationServers()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{ "resource": "https://api.example.com/mcp", "authorization_servers": ["https://idp.example.com"], "scopes_supported": ["mcp.read"] }"""));
        using var http = new HttpClient(stub);
        var client = new OAuthDiscoveryClient(http);

        var prm = await client.FetchProtectedResourceMetadataAsync(
            new Uri("https://example.com/.well-known/oauth-protected-resource/mcp"),
            CancellationToken.None);

        prm.ShouldNotBeNull();
        prm.Resource.ShouldBe("https://api.example.com/mcp");
        prm.AuthorizationServers.ShouldBe(new[] { "https://idp.example.com" });
        prm.ScopesSupported.ShouldBe(new[] { "mcp.read" });
    }

    [Fact]
    public async Task FetchProtectedResourceMetadata_500_Throws()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(stub);
        var client = new OAuthDiscoveryClient(http);

        await Should.ThrowAsync<McpLenseAuthException>(() =>
            client.FetchProtectedResourceMetadataAsync(new Uri("https://example.com/.well-known/oauth-protected-resource"), CancellationToken.None));
    }

    [Fact]
    public async Task FetchAuthorizationServerMetadata_HitsWellKnownAndParses()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{ "issuer": "https://idp.example.com", "authorization_endpoint": "https://idp.example.com/oauth2/authorize", "token_endpoint": "https://idp.example.com/oauth2/token", "registration_endpoint": "https://idp.example.com/oauth2/register" }"""));
        using var http = new HttpClient(stub);
        var client = new OAuthDiscoveryClient(http);

        var asm = await client.FetchAuthorizationServerMetadataAsync(
            new Uri("https://idp.example.com"),
            CancellationToken.None);

        stub.Requests.Single().RequestUri.ShouldBe(new Uri("https://idp.example.com/.well-known/oauth-authorization-server"));
        asm.AuthorizationEndpoint.ShouldBe("https://idp.example.com/oauth2/authorize");
        asm.TokenEndpoint.ShouldBe("https://idp.example.com/oauth2/token");
        asm.RegistrationEndpoint.ShouldBe("https://idp.example.com/oauth2/register");
    }

    [Fact]
    public async Task FetchAuthorizationServerMetadata_MissingEndpoints_Throws()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{ "issuer": "https://idp" }"""));
        using var http = new HttpClient(stub);
        var client = new OAuthDiscoveryClient(http);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            client.FetchAuthorizationServerMetadataAsync(new Uri("https://idp"), CancellationToken.None));
        ex.Message.ShouldContain("authorization_endpoint");
    }

    [Theory]
    [InlineData("https://idp.example.com/", "https://idp.example.com/.well-known/oauth-authorization-server")]
    [InlineData("https://idp.example.com/tenant1", "https://idp.example.com/tenant1/.well-known/oauth-authorization-server")]
    [InlineData("https://idp.example.com/tenant1/", "https://idp.example.com/tenant1/.well-known/oauth-authorization-server")]
    public void BuildAsmAppendUri_AppendsWellKnownAfterPath(string issuer, string expected)
    {
        var uri = OAuthDiscoveryClient.BuildAsmAppendUri(new Uri(issuer));

        uri.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://idp.example.com/", "https://idp.example.com/.well-known/openid-configuration")]
    [InlineData("https://idp.example.com/tenant1", "https://idp.example.com/tenant1/.well-known/openid-configuration")]
    [InlineData("https://login.microsoftonline.com/organizations/v2.0", "https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration")]
    public void BuildOidcUri_AppendsOpenidConfiguration(string issuer, string expected)
    {
        var uri = OAuthDiscoveryClient.BuildOidcUri(new Uri(issuer));

        uri.ToString().ShouldBe(expected);
    }

    [Fact]
    public async Task FetchAsm_InsertReturns404_AppendSucceeds_ReturnsParsed()
    {
        var router = new RoutingHandler();
        router.Routes["https://idp.example.com/.well-known/oauth-authorization-server/tenant1"] =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        router.Routes["https://idp.example.com/tenant1/.well-known/oauth-authorization-server"] =
            _ => Json(HttpStatusCode.OK,
                """{ "issuer": "https://idp.example.com/tenant1", "authorization_endpoint": "https://idp.example.com/tenant1/authorize", "token_endpoint": "https://idp.example.com/tenant1/token" }""");

        using var http = new HttpClient(router);
        var client = new OAuthDiscoveryClient(http);

        var asm = await client.FetchAuthorizationServerMetadataAsync(
            new Uri("https://idp.example.com/tenant1"),
            CancellationToken.None);

        asm.AuthorizationEndpoint.ShouldBe("https://idp.example.com/tenant1/authorize");
        asm.TokenEndpoint.ShouldBe("https://idp.example.com/tenant1/token");
        router.Requests.Select(r => r.RequestUri!.ToString()).ShouldBe(new[]
        {
            "https://idp.example.com/.well-known/oauth-authorization-server/tenant1",
            "https://idp.example.com/tenant1/.well-known/oauth-authorization-server"
        });
    }

    [Fact]
    public async Task FetchAsm_InsertAndAppendReturn404_OidcSucceeds_ReturnsParsed()
    {
        var router = new RoutingHandler();
        router.Routes["https://login.microsoftonline.com/.well-known/oauth-authorization-server/organizations/v2.0"] =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        router.Routes["https://login.microsoftonline.com/organizations/v2.0/.well-known/oauth-authorization-server"] =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        // Real-world Microsoft Entra v2.0 OIDC document, trimmed to the fields McpLense reads.
        router.Routes["https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration"] =
            _ => Json(HttpStatusCode.OK,
                """
                {
                  "issuer": "https://login.microsoftonline.com/{tenantid}/v2.0",
                  "authorization_endpoint": "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize",
                  "token_endpoint": "https://login.microsoftonline.com/organizations/oauth2/v2.0/token",
                  "scopes_supported": ["openid","profile","email","offline_access"],
                  "token_endpoint_auth_methods_supported": ["client_secret_post","private_key_jwt","client_secret_basic"]
                }
                """);

        using var http = new HttpClient(router);
        var client = new OAuthDiscoveryClient(http);

        var asm = await client.FetchAuthorizationServerMetadataAsync(
            new Uri("https://login.microsoftonline.com/organizations/v2.0"),
            CancellationToken.None);

        asm.AuthorizationEndpoint.ShouldBe("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize");
        asm.TokenEndpoint.ShouldBe("https://login.microsoftonline.com/organizations/oauth2/v2.0/token");
        asm.Issuer.ShouldBe("https://login.microsoftonline.com/{tenantid}/v2.0");
        asm.RegistrationEndpoint.ShouldBeNull();
        asm.ScopesSupported.ShouldBe(new[] { "openid", "profile", "email", "offline_access" });
        router.Requests.Select(r => r.RequestUri!.ToString()).ShouldBe(new[]
        {
            "https://login.microsoftonline.com/.well-known/oauth-authorization-server/organizations/v2.0",
            "https://login.microsoftonline.com/organizations/v2.0/.well-known/oauth-authorization-server",
            "https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration"
        });
    }

    [Fact]
    public async Task FetchAsm_AllThreeReturn404_Throws_ListsAllAttempts()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(stub);
        var client = new OAuthDiscoveryClient(http);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            client.FetchAuthorizationServerMetadataAsync(
                new Uri("https://login.microsoftonline.com/organizations/v2.0"),
                CancellationToken.None));

        ex.Message.ShouldContain("could not be located for issuer 'https://login.microsoftonline.com/organizations/v2.0'");
        ex.Message.ShouldContain("https://login.microsoftonline.com/.well-known/oauth-authorization-server/organizations/v2.0");
        ex.Message.ShouldContain("https://login.microsoftonline.com/organizations/v2.0/.well-known/oauth-authorization-server");
        ex.Message.ShouldContain("https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration");
        ex.Message.ShouldContain("HTTP 404");
        // All three forms must have been tried (issuer has a non-empty path so all three URIs differ).
        stub.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task FetchAsm_FirstFormReturnsInvalidJson_Throws_DoesNotFallBack()
    {
        // A 2xx response with broken JSON at the strict-spec URL clearly meant to respond there;
        // silently falling through would mask the real bug. Verify we stop and surface it.
        var router = new RoutingHandler();
        router.Routes["https://idp.example.com/.well-known/oauth-authorization-server/tenant1"] =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ not valid json", Encoding.UTF8, "application/json")
            };
        router.Routes["https://idp.example.com/tenant1/.well-known/oauth-authorization-server"] =
            _ => Json(HttpStatusCode.OK,
                """{ "issuer": "x", "authorization_endpoint": "x", "token_endpoint": "x" }""");

        using var http = new HttpClient(router);
        var client = new OAuthDiscoveryClient(http);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            client.FetchAuthorizationServerMetadataAsync(
                new Uri("https://idp.example.com/tenant1"),
                CancellationToken.None));

        ex.Message.ShouldContain("not valid JSON");
        ex.Message.ShouldContain("https://idp.example.com/.well-known/oauth-authorization-server/tenant1");
        // Must NOT have tried the append/oidc fallbacks.
        router.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FetchAsm_FirstFormReturnsValidJsonMissingEndpoints_Throws_DoesNotFallBack()
    {
        // Same principle for "valid JSON but missing required endpoints": surface the failure
        // at the URL the server actually responded to, don't silently try the fallbacks.
        var router = new RoutingHandler();
        router.Routes["https://idp.example.com/.well-known/oauth-authorization-server/tenant1"] =
            _ => Json(HttpStatusCode.OK, """{ "issuer": "https://idp.example.com/tenant1" }""");
        router.Routes["https://idp.example.com/tenant1/.well-known/oauth-authorization-server"] =
            _ => Json(HttpStatusCode.OK,
                """{ "issuer": "x", "authorization_endpoint": "x", "token_endpoint": "x" }""");

        using var http = new HttpClient(router);
        var client = new OAuthDiscoveryClient(http);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            client.FetchAuthorizationServerMetadataAsync(
                new Uri("https://idp.example.com/tenant1"),
                CancellationToken.None));

        ex.Message.ShouldContain("authorization_endpoint");
        ex.Message.ShouldContain("https://idp.example.com/.well-known/oauth-authorization-server/tenant1");
        router.Requests.Count.ShouldBe(1);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } =
            new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var key = request.RequestUri!.ToString();
            if (!Routes.TryGetValue(key, out var factory))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    ReasonPhrase = $"unrouted: {key}"
                });
            }

            return Task.FromResult(factory(request));
        }
    }
}
