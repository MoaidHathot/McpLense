using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class BearerHandlerTests
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

    [Fact]
    public void Constructor_NullToken_Throws()
    {
        Should.Throw<ArgumentException>(() => new BearerHandler(null!));
    }

    [Fact]
    public void Constructor_EmptyToken_Throws()
    {
        Should.Throw<ArgumentException>(() => new BearerHandler(string.Empty));
    }

    [Fact]
    public async Task SendAsync_AddsBearerAuthorization()
    {
        var inner = new CapturingInner();
        var handler = new BearerHandler("abc") { InnerHandler = inner };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/");

        inner.LastRequest.ShouldNotBeNull();
        inner.LastRequest.Headers.Authorization.ShouldNotBeNull();
        inner.LastRequest.Headers.Authorization.Scheme.ShouldBe("Bearer");
        inner.LastRequest.Headers.Authorization.Parameter.ShouldBe("abc");
    }

    [Fact]
    public async Task SendAsync_OverwritesExistingAuthorization()
    {
        var inner = new CapturingInner();
        var handler = new BearerHandler("new-token") { InnerHandler = inner };
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stale");

        await client.SendAsync(request);

        inner.LastRequest.ShouldNotBeNull();
        inner.LastRequest.Headers.Authorization!.Parameter.ShouldBe("new-token");
    }
}
