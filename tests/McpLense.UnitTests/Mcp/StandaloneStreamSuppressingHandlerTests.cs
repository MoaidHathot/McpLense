using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

public class StandaloneStreamSuppressingHandlerTests
{
    /// <summary>Records whether the inner handler was reached and what it was asked to send.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task StandaloneGetStream_IsSuppressedLocally_WithMethodNotAllowed()
    {
        var capturing = new CapturingHandler();
        using var client = new HttpClient(new StandaloneStreamSuppressingHandler(capturing));

        // The Streamable HTTP standalone stream: GET carrying an Mcp-Session-Id.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/mcp");
        request.Headers.Add("Mcp-Session-Id", "abc123");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        // It must never reach the network - that early GET is what corrupts the session.
        capturing.Captured.ShouldBeNull();
    }

    [Fact]
    public async Task PostRequests_PassThroughUntouched()
    {
        var capturing = new CapturingHandler();
        using var client = new HttpClient(new StandaloneStreamSuppressingHandler(capturing));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/mcp");
        request.Headers.Add("Mcp-Session-Id", "abc123");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        capturing.Captured.ShouldNotBeNull();
    }

    [Fact]
    public async Task LegacySseGet_WithoutSessionHeader_IsNotSuppressed()
    {
        // The legacy HTTP+SSE transport opens its primary GET stream with no Mcp-Session-Id header.
        // That must still reach the server, or SSE servers would break.
        var capturing = new CapturingHandler();
        using var client = new HttpClient(new StandaloneStreamSuppressingHandler(capturing));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/sse");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        capturing.Captured.ShouldNotBeNull();
    }
}
