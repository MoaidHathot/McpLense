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

public class AuthProbeTests
{
    /// <summary>
    /// Fake <see cref="HttpMessageHandler"/> that returns a queued response for every call. Used
    /// to simulate the multi-hop probe (HEAD -> GET fallback, then PRM fetch) without a real HTTP
    /// stack.
    /// </summary>
    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string? body = null, string? wwwAuthenticate = null)
        {
            var response = new HttpResponseMessage(status);
            if (wwwAuthenticate is not null)
            {
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", wwwAuthenticate);
            }

            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            _responses.Enqueue(response);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("QueuedHandler ran out of responses.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static (AuthProbe Probe, QueuedHandler Handler, List<string> StderrSink) Build()
    {
        var handler = new QueuedHandler();
        var http = new HttpClient(handler);
        var stderr = new List<string>();
        var probe = new AuthProbe(http, stderr.Add);
        return (probe, handler, stderr);
    }

    [Fact]
    public async Task ProbeAsync_Server200NoHeaders_ReturnsEmpty()
    {
        var (probe, handler, _) = Build();
        handler.Enqueue(HttpStatusCode.OK);

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.IsEmpty.ShouldBeTrue();
        result.RequiresAuth.ShouldBeFalse();
    }

    [Fact]
    public async Task ProbeAsync_Server401NoMetadata_RequiresAuthButNoMetadata()
    {
        var (probe, handler, stderr) = Build();
        handler.Enqueue(HttpStatusCode.Unauthorized, body: "Unauthorized");

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.RequiresAuth.ShouldBeTrue();
        result.ResourceMetadataUrl.ShouldBeNull();
        stderr.Count.ShouldBeGreaterThan(0);
        string.Join(" ", stderr).ShouldContain("no RFC 9728");
    }

    [Fact]
    public async Task ProbeAsync_Server401WithResourceMetadata_FetchesAndParses()
    {
        var (probe, handler, _) = Build();

        // First response: HEAD returns 401 + WWW-Authenticate pointing at the PRM document.
        handler.Enqueue(
            HttpStatusCode.Unauthorized,
            wwwAuthenticate: "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\"");

        // Second response: PRM document.
        const string metadata = """
        {
          "resource": "https://example.com/",
          "scopes_supported": ["mcp.read", "mcp.write"],
          "authorization_servers": ["https://login.example.com"]
        }
        """;
        handler.Enqueue(HttpStatusCode.OK, body: metadata);

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.RequiresAuth.ShouldBeTrue();
        result.ResourceMetadataUrl.ShouldBe("https://example.com/.well-known/oauth-protected-resource");
        result.Scopes!.ShouldBe(new[] { "mcp.read", "mcp.write" });
        result.AuthorizationServers!.ShouldBe(new[] { "https://login.example.com" });
        result.Resource.ShouldBe("https://example.com/");
    }

    [Fact]
    public async Task ProbeAsync_PrmWithoutResourceField_LeavesResourceNull()
    {
        // RFC 9728 nominally requires "resource", but production servers in the wild sometimes
        // omit it. The probe must tolerate the absence without crashing or treating an empty
        // value as a real resource URI (which would mis-qualify bare scope names downstream).
        var (probe, handler, _) = Build();
        handler.Enqueue(
            HttpStatusCode.Unauthorized,
            wwwAuthenticate: "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\"");
        handler.Enqueue(HttpStatusCode.OK, body: """{"scopes_supported": ["mcp.read"]}""");

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.RequiresAuth.ShouldBeTrue();
        result.Scopes!.ShouldBe(new[] { "mcp.read" });
        result.Resource.ShouldBeNull();
    }

    [Fact]
    public async Task ProbeAsync_UsesGet_NotHead()
    {
        // Some MCP servers (Agent365 in particular) hang on HEAD requests, taking 30+ seconds
        // to time out. The probe must use GET to stay responsive. We only read response headers
        // (HttpCompletionOption.ResponseHeadersRead) so the body-download cost is irrelevant.
        var (probe, handler, _) = Build();
        handler.Enqueue(HttpStatusCode.OK);

        await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public async Task ProbeAsync_NetworkError_LogsAndIsInconclusive()
    {
        var stderr = new List<string>();
        var failingHandler = new HttpClientHandler();
        // Force connection refused by pointing at port 1.
        var http = new HttpClient(failingHandler) { Timeout = TimeSpan.FromSeconds(2) };
        var probe = new AuthProbe(http, stderr.Add);

        var result = await probe.ProbeAsync(new Uri("http://127.0.0.1:1/"), CancellationToken.None);

        // Network failure is inconclusive (NOT "no auth needed") so callers with loaded
        // profiles still attach one rather than connect plain and hit the same failure twice.
        result.Inconclusive.ShouldBeTrue();
        result.IsEmpty.ShouldBeFalse();
        stderr.Count.ShouldBeGreaterThan(0);
        var joined = string.Join(" ", stderr);
        joined.ShouldContain("probing");
        joined.ShouldContain("attaching the configured profile");
    }

    [Fact]
    public async Task ProbeAsync_InconclusiveResult_IsNotEmpty()
    {
        // Sanity check the IsEmpty / Inconclusive interaction.
        new AuthProbeResult(Inconclusive: true).IsEmpty.ShouldBeFalse();
        new AuthProbeResult().IsEmpty.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProbeAsync_PrmReturnsNon2xx_RequiresAuthSetButNoFurtherFields()
    {
        var (probe, handler, stderr) = Build();
        handler.Enqueue(
            HttpStatusCode.Unauthorized,
            wwwAuthenticate: "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\"");
        handler.Enqueue(HttpStatusCode.NotFound, body: "missing");

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.RequiresAuth.ShouldBeTrue();
        result.ResourceMetadataUrl.ShouldBe("https://example.com/.well-known/oauth-protected-resource");
        result.Scopes.ShouldBeNull();
        string.Join(" ", stderr).ShouldContain("404");
    }

    [Fact]
    public async Task ProbeAsync_PrmReturnsMalformedJson_LogsAndReturnsRequiresAuth()
    {
        var (probe, handler, stderr) = Build();
        handler.Enqueue(
            HttpStatusCode.Unauthorized,
            wwwAuthenticate: "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\"");
        handler.Enqueue(HttpStatusCode.OK, body: "{not-json");

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.RequiresAuth.ShouldBeTrue();
        result.ResourceMetadataUrl.ShouldBe("https://example.com/.well-known/oauth-protected-resource");
        string.Join(" ", stderr).ShouldContain("not valid JSON");
    }

    [Fact]
    public async Task ProbeAsync_PrmUrlNotAbsolute_Logs_RequiresAuthOnly()
    {
        var (probe, handler, stderr) = Build();
        handler.Enqueue(
            HttpStatusCode.Unauthorized,
            wwwAuthenticate: "Bearer resource_metadata=\"/relative/url\"");

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        // Header was present (so RequiresAuth=true), but the URL was unusable.
        result.RequiresAuth.ShouldBeTrue();
        result.Scopes.ShouldBeNull();
        result.AuthorizationServers.ShouldBeNull();
        string.Join(" ", stderr).ShouldContain("not absolute");
    }

    [Fact]
    public async Task ProbeAsync_Server503NoAuthChallenge_IsInconclusive()
    {
        // Some MCP servers (e.g. Agent365) return 503 to unauthenticated HEAD requests because
        // they execute auth middleware before the application code that would respond properly.
        // Treat non-2xx without an explicit challenge as inconclusive (not 'auth required') so
        // we don't claim something we can't prove, but still signal callers to attach a loaded
        // profile rather than connect plain.
        var (probe, handler, stderr) = Build();
        handler.Enqueue(System.Net.HttpStatusCode.ServiceUnavailable, body: """{"code":"UnexpectedError","message":"An unexpected error occurred."}""");

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.RequiresAuth.ShouldBeFalse();
        result.Inconclusive.ShouldBeTrue();
        result.IsEmpty.ShouldBeFalse();
        result.ResourceMetadataUrl.ShouldBeNull();
        string.Join(" ", stderr).ShouldContain("503");
        string.Join(" ", stderr).ShouldContain("inconclusive");
    }

    [Fact]
    public async Task ProbeAsync_Server500NoAuthChallenge_IsInconclusive()
    {
        var (probe, handler, _) = Build();
        handler.Enqueue(System.Net.HttpStatusCode.InternalServerError);

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.RequiresAuth.ShouldBeFalse();
        result.Inconclusive.ShouldBeTrue();
    }

    [Fact]
    public async Task ProbeAsync_Server404NoAuthChallenge_IsInconclusive()
    {
        // 404 without an auth challenge is ambiguous. Inconclusive lets the caller decide
        // (e.g. attach a loaded profile and let the runtime path surface the real error).
        var (probe, handler, _) = Build();
        handler.Enqueue(System.Net.HttpStatusCode.NotFound);

        var result = await probe.ProbeAsync(new Uri("https://example.com/"), CancellationToken.None);

        result.RequiresAuth.ShouldBeFalse();
        result.Inconclusive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Bearer", null)]
    [InlineData("Bearer realm=\"foo\"", null)]
    [InlineData("Bearer resource_metadata=\"https://x/y\"", "https://x/y")]
    [InlineData("Bearer realm=\"foo\", resource_metadata=\"https://x/y\"", "https://x/y")]
    [InlineData("Bearer resource_metadata=\"https://x/y\", scope=\"mcp.read\"", "https://x/y")]
    public void TryExtractResourceMetadataUrl_ParsesVariousChallengeShapes(string headerValue, string? expected)
    {
        var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", headerValue);

        AuthProbe.TryExtractResourceMetadataUrl(response).ShouldBe(expected);
    }

    [Fact]
    public void TryExtractResourceMetadataUrl_NoBearerChallenge_ReturnsNull()
    {
        var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Basic realm=\"foo\"");

        AuthProbe.TryExtractResourceMetadataUrl(response).ShouldBeNull();
    }

    [Fact]
    public void TryExtractResourceMetadataUrl_NoHeader_ReturnsNull()
    {
        var response = new HttpResponseMessage();

        AuthProbe.TryExtractResourceMetadataUrl(response).ShouldBeNull();
    }

    // -------- Per-URL caching (memoiser) ------------------------------------------

    [Fact]
    public async Task ProbeAsync_SameUrlTwice_OnlyHitsHttpOnce()
    {
        // Critical invariant: the resolver and the executor both probe the same URL during a
        // single server resolution. The probe must cache per-URL to keep that to ONE HTTP
        // round-trip; otherwise users see double "AuthProbe:" stderr lines and double wait time
        // on slow servers (e.g. Agent365 502/timeout on unauthenticated HEAD).
        var (probe, handler, _) = Build();
        handler.Enqueue(HttpStatusCode.OK);

        var url = new Uri("https://example.com/");
        var first = await probe.ProbeAsync(url, CancellationToken.None);
        var second = await probe.ProbeAsync(url, CancellationToken.None);

        handler.Requests.Count.ShouldBe(1);
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public async Task ProbeAsync_DifferentUrls_HitHttpIndependently()
    {
        var (probe, handler, _) = Build();
        handler.Enqueue(HttpStatusCode.OK);
        handler.Enqueue(HttpStatusCode.OK);

        await probe.ProbeAsync(new Uri("https://a.example.com/"), CancellationToken.None);
        await probe.ProbeAsync(new Uri("https://b.example.com/"), CancellationToken.None);

        handler.Requests.Count.ShouldBe(2);
    }
}
