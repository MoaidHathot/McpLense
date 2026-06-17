using System;
using System.Collections.Generic;
using System.Net.Http;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

public class McpHttpClientFactoryTests
{
    private static ResolvedServer HttpServer(ResolvedAuth? auth = null)
        => new(
            Name: "s",
            Kind: ConnectionKind.Http,
            Target: "https://example.test/mcp",
            Source: null,
            Command: null,
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: new Uri("https://example.test/mcp"),
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Auth: auth);

    [Fact]
    public void Default_SuppressesStandaloneStream_OverSockets()
    {
        var chain = McpHttpClientFactory.BuildHandlerChain(HttpServer(), auth: null, suppressStandaloneStream: true);

        var suppressor = chain.ShouldBeOfType<StandaloneStreamSuppressingHandler>();
        suppressor.InnerHandler.ShouldBeOfType<SocketsHttpHandler>();
    }

    [Fact]
    public void OptOut_OmitsTheSuppressor()
    {
        // The server-initiated observation check (and --server-stream) need the standalone stream.
        var chain = McpHttpClientFactory.BuildHandlerChain(HttpServer(), auth: null, suppressStandaloneStream: false);

        chain.ShouldBeOfType<SocketsHttpHandler>();
    }

    [Fact]
    public void WithAuth_ChainsAuthHandlerBetweenSuppressorAndSockets()
    {
        var auth = new ResolvedAuth(AuthKind.Bearer, Token: "t");

        var chain = McpHttpClientFactory.BuildHandlerChain(HttpServer(auth), auth, suppressStandaloneStream: true);

        var suppressor = chain.ShouldBeOfType<StandaloneStreamSuppressingHandler>();
        var authHandler = suppressor.InnerHandler.ShouldBeAssignableTo<DelegatingHandler>();
        authHandler!.InnerHandler.ShouldBeOfType<SocketsHttpHandler>();
    }
}
