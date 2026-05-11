using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class InteractiveBrowserHandlerTests
{
    private sealed class CapturingInner : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class RecordingTokenCredential : TokenCredential
    {
        private readonly Func<TokenRequestContext, AccessToken> _factory;
        public List<TokenRequestContext> Requests { get; } = new();

        public RecordingTokenCredential(Func<TokenRequestContext, AccessToken> factory)
        {
            _factory = factory;
        }

        public RecordingTokenCredential(string token, DateTimeOffset? expires = null)
            : this(_ => new AccessToken(token, expires ?? DateTimeOffset.UtcNow.AddHours(1)))
        {
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Add(requestContext);
            return _factory(requestContext);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Add(requestContext);
            return new ValueTask<AccessToken>(_factory(requestContext));
        }
    }

    private sealed class ThrowingTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated MSAL failure");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated MSAL failure");
    }

    [Fact]
    public void Constructor_NullCredential_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new InteractiveBrowserHandler(null!, new[] { "s" }));
    }

    [Fact]
    public void Constructor_NullScopes_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new InteractiveBrowserHandler(new RecordingTokenCredential("t"), null!));
    }

    [Fact]
    public void Constructor_EmptyScopes_Throws()
    {
        Should.Throw<ArgumentException>(() => new InteractiveBrowserHandler(new RecordingTokenCredential("t"), Array.Empty<string>()));
    }

    [Fact]
    public async Task SendAsync_AttachesBearerHeaderFromCredential()
    {
        var credential = new RecordingTokenCredential("abc123");
        var inner = new CapturingInner();
        var handler = new InteractiveBrowserHandler(credential, new[] { "api://x/.default" }) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/mcp");

        inner.LastRequest.ShouldNotBeNull();
        inner.LastRequest.Headers.Authorization.ShouldNotBeNull();
        inner.LastRequest.Headers.Authorization.Scheme.ShouldBe("Bearer");
        inner.LastRequest.Headers.Authorization.Parameter.ShouldBe("abc123");
    }

    [Fact]
    public async Task SendAsync_PassesConfiguredScopesToCredential()
    {
        var credential = new RecordingTokenCredential("tok");
        var inner = new CapturingInner();
        var scopes = new[] { "api://resource/.default", "offline_access" };
        var handler = new InteractiveBrowserHandler(credential, scopes) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/mcp");

        credential.Requests.ShouldHaveSingleItem();
        credential.Requests[0].Scopes.ShouldBe(scopes);
    }

    [Fact]
    public async Task SendAsync_AcquiresTokenOnEveryRequest()
    {
        // Per-request token acquisition keeps the MSAL cache as the single source of truth, so
        // refreshed tokens get picked up automatically without bespoke caching in this handler.
        var credential = new RecordingTokenCredential("tok");
        var inner = new CapturingInner();
        var handler = new InteractiveBrowserHandler(credential, new[] { "s" }) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/mcp");
        await client.GetAsync("https://example.com/mcp/again");

        credential.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SendAsync_CredentialFailure_RaisesMcpLenseAuthException()
    {
        var handler = new InteractiveBrowserHandler(new ThrowingTokenCredential(), new[] { "s" })
        {
            InnerHandler = new CapturingInner()
        };
        using var client = new HttpClient(handler);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() => client.GetAsync("https://example.com/mcp"));
        ex.Message.ShouldContain("interactive-browser");
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task SendAsync_OverwritesPreExistingAuthorizationHeader()
    {
        var credential = new RecordingTokenCredential("fresh");
        var inner = new CapturingInner();
        var handler = new InteractiveBrowserHandler(credential, new[] { "s" }) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/mcp");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "stale");

        await client.SendAsync(request);

        inner.LastRequest!.Headers.Authorization!.Parameter.ShouldBe("fresh");
    }
}
