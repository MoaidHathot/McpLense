using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class AuthorizationServerProbeTests
{
    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string? Body)> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string? body = null) => _responses.Enqueue((status, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Queue empty.");
            }
            var (status, body) = _responses.Dequeue();
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        }
    }

    private static AuthorizationServerProbe Build(out QueuedHandler handler)
    {
        handler = new QueuedHandler();
        return new AuthorizationServerProbe(new HttpClient(handler));
    }

    [Fact]
    public async Task Probe_AsMetadataAvailable_ParsesAllSecurityRelevantFields()
    {
        // Verbatim parse of every field we care about. Probe tries /oauth-authorization-server
        // first - if that succeeds we never hit /openid-configuration.
        var probe = Build(out var handler);
        handler.Enqueue(HttpStatusCode.OK, """
        {
          "issuer": "https://login.example.com",
          "authorization_endpoint": "https://login.example.com/authorize",
          "token_endpoint": "https://login.example.com/token",
          "registration_endpoint": "https://login.example.com/register",
          "introspection_endpoint": "https://login.example.com/introspect",
          "revocation_endpoint": "https://login.example.com/revoke",
          "jwks_uri": "https://login.example.com/jwks",
          "scopes_supported": ["openid", "profile"],
          "response_types_supported": ["code"],
          "grant_types_supported": ["authorization_code", "refresh_token"],
          "token_endpoint_auth_methods_supported": ["client_secret_basic", "none"],
          "code_challenge_methods_supported": ["S256"],
          "resource_parameter_supported": true,
          "custom_vendor_field": "preserved-in-raw"
        }
        """);

        var info = await probe.ProbeAsync("https://login.example.com", CancellationToken.None);

        info.Fetched.ShouldBeTrue();
        info.FetchError.ShouldBeNull();
        info.AuthorizationEndpoint.ShouldBe("https://login.example.com/authorize");
        info.TokenEndpoint.ShouldBe("https://login.example.com/token");
        info.RegistrationEndpoint.ShouldBe("https://login.example.com/register");
        info.IntrospectionEndpoint.ShouldBe("https://login.example.com/introspect");
        info.RevocationEndpoint.ShouldBe("https://login.example.com/revoke");
        info.JwksUri.ShouldBe("https://login.example.com/jwks");
        info.ScopesSupported.ShouldBe(new[] { "openid", "profile" });
        info.ResponseTypesSupported.ShouldBe(new[] { "code" });
        info.GrantTypesSupported.ShouldBe(new[] { "authorization_code", "refresh_token" });
        info.TokenEndpointAuthMethodsSupported.ShouldBe(new[] { "client_secret_basic", "none" });
        info.CodeChallengeMethodsSupported.ShouldBe(new[] { "S256" });
        info.ResourceParameterSupported.ShouldBe(true);

        // Raw JSON survives so consumers can inspect vendor fields like custom_vendor_field.
        info.Raw.ShouldNotBeNull();
        info.Raw!["custom_vendor_field"]!.GetValue<string>().ShouldBe("preserved-in-raw");

        // Only one request fired (the first /oauth-authorization-server hit succeeded).
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.AbsolutePath.ShouldEndWith("/.well-known/oauth-authorization-server");
    }

    [Fact]
    public async Task Probe_OAuthMetadataMissing_FallsBackToOidcDiscovery()
    {
        // Many issuers (Entra, Google) only publish /.well-known/openid-configuration. The
        // probe tries the OAuth path first then OIDC; both URLs are probed in order.
        var probe = Build(out var handler);
        handler.Enqueue(HttpStatusCode.NotFound);
        handler.Enqueue(HttpStatusCode.OK, """
        {
          "issuer": "https://login.example.com",
          "token_endpoint": "https://login.example.com/token"
        }
        """);

        var info = await probe.ProbeAsync("https://login.example.com", CancellationToken.None);

        info.Fetched.ShouldBeTrue();
        info.TokenEndpoint.ShouldBe("https://login.example.com/token");
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.AbsolutePath.ShouldEndWith("/.well-known/oauth-authorization-server");
        handler.Requests[1].RequestUri!.AbsolutePath.ShouldEndWith("/.well-known/openid-configuration");
    }

    [Fact]
    public async Task Probe_BothEndpointsFail_ReturnsFetchedFalseWithLastError()
    {
        var probe = Build(out var handler);
        handler.Enqueue(HttpStatusCode.NotFound);
        handler.Enqueue(HttpStatusCode.InternalServerError);

        var info = await probe.ProbeAsync("https://login.example.com", CancellationToken.None);

        info.Fetched.ShouldBeFalse();
        info.FetchError.ShouldNotBeNull();
        // The last error wins; the probe doesn't synthesise a summary across both attempts.
        info.FetchError!.ShouldContain("500");
    }

    [Fact]
    public async Task Probe_IssuerWithTrailingSlash_NormalisesUrl()
    {
        // RFC 8414 requires the issuer to have no trailing slash for path-joining, but real
        // implementations are loose about it. The probe must accept both forms identically.
        var probe = Build(out var handler);
        handler.Enqueue(HttpStatusCode.OK, """{"issuer": "https://login.example.com"}""");

        await probe.ProbeAsync("https://login.example.com/", CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString()
            .ShouldBe("https://login.example.com/.well-known/oauth-authorization-server");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://issuer.example.com")]
    [InlineData("file:///etc/passwd")]
    public async Task Probe_NonHttpIssuer_RejectsImmediatelyWithoutNetwork(string maliciousIssuer)
    {
        // A malicious or buggy server could advertise a non-http(s) issuer URL. We must
        // reject these up front - never make a network call to schemes other than http(s),
        // and never throw past the probe boundary.
        var probe = Build(out var handler);

        var info = await probe.ProbeAsync(maliciousIssuer, CancellationToken.None);

        info.Fetched.ShouldBeFalse();
        info.FetchError.ShouldNotBeNull();
        info.FetchError!.ShouldContain("absolute http(s) URL");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Probe_InvalidJson_ReportsParseError()
    {
        var probe = Build(out var handler);
        handler.Enqueue(HttpStatusCode.OK, "{not-json");

        var info = await probe.ProbeAsync("https://login.example.com", CancellationToken.None);

        info.Fetched.ShouldBeFalse();
        info.FetchError.ShouldNotBeNull();
        info.FetchError!.ShouldContain("parse");
    }
}
