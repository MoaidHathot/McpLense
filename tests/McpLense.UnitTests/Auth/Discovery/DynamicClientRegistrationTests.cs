using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth.Discovery;

public class DynamicClientRegistrationTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return _factory(request);
        }
    }

    [Fact]
    public async Task RegisterAsync_PostsRfc7591Body()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{ "client_id": "abc-123" }""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(stub);
        var dcr = new DynamicClientRegistration(http);

        var response = await dcr.RegisterAsync(
            new Uri("https://idp/oauth2/register"),
            "http://127.0.0.1:5050/callback",
            scopes: new[] { "mcp.read", "mcp.write" },
            CancellationToken.None);

        response.ClientId.ShouldBe("abc-123");

        var sent = stub.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.RequestUri.ShouldBe(new Uri("https://idp/oauth2/register"));

        var body = JsonNode.Parse(stub.Bodies.Single())!.AsObject();
        body["client_name"]!.GetValue<string>().ShouldBe("McpLense");
        body["redirect_uris"]!.AsArray()[0]!.GetValue<string>().ShouldBe("http://127.0.0.1:5050/callback");
        body["grant_types"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(new[] { "authorization_code", "refresh_token" });
        body["response_types"]!.AsArray()[0]!.GetValue<string>().ShouldBe("code");
        body["token_endpoint_auth_method"]!.GetValue<string>().ShouldBe("none");
        body["application_type"]!.GetValue<string>().ShouldBe("native");
        body["scope"]!.GetValue<string>().ShouldBe("mcp.read mcp.write");
    }

    [Fact]
    public async Task RegisterAsync_NoScopes_OmitsScopeField()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{ "client_id": "x" }""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(stub);
        var dcr = new DynamicClientRegistration(http);

        await dcr.RegisterAsync(new Uri("https://idp/register"), "http://cb", scopes: null, CancellationToken.None);

        var body = JsonNode.Parse(stub.Bodies.Single())!.AsObject();
        body.ContainsKey("scope").ShouldBeFalse();
    }

    [Fact]
    public async Task RegisterAsync_MissingClientId_Throws()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{ }""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(stub);
        var dcr = new DynamicClientRegistration(http);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            dcr.RegisterAsync(new Uri("https://idp/register"), "http://cb", null, CancellationToken.None));
        ex.Message.ShouldContain("client_id");
    }

    [Fact]
    public async Task RegisterAsync_HttpError_PropagatesBodyInMessage()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{ "error": "invalid_redirect_uri" }""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(stub);
        var dcr = new DynamicClientRegistration(http);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() =>
            dcr.RegisterAsync(new Uri("https://idp/register"), "http://cb", null, CancellationToken.None));
        ex.Message.ShouldContain("invalid_redirect_uri");
        ex.Message.ShouldContain("400");
    }
}
