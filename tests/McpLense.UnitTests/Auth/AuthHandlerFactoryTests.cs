using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class AuthHandlerFactoryTests
{
    [Fact]
    public void Create_None_ReturnsNull()
    {
        var auth = new ResolvedAuth(AuthKind.None);

        AuthHandlerFactory.Create(auth).ShouldBeNull();
    }

    [Fact]
    public void Create_Bearer_ReturnsBearerHandler()
    {
        var auth = new ResolvedAuth(AuthKind.Bearer, Token: "abc");

        var handler = AuthHandlerFactory.Create(auth);

        handler.ShouldBeOfType<BearerHandler>();
    }

    [Fact]
    public void Create_BearerMissingToken_Throws()
    {
        var auth = new ResolvedAuth(AuthKind.Bearer, Token: null);

        Should.Throw<McpLenseAuthException>(() => AuthHandlerFactory.Create(auth));
    }

    [Fact]
    public void Create_BearerEmptyToken_Throws()
    {
        var auth = new ResolvedAuth(AuthKind.Bearer, Token: string.Empty);

        Should.Throw<McpLenseAuthException>(() => AuthHandlerFactory.Create(auth));
    }

    [Fact]
    public void Create_OAuth_ReturnsDiscoveryHandler()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth, Scopes: new[] { "mcp.read" });
        var serverUrl = new Uri("https://example.com/mcp");

        var handler = AuthHandlerFactory.Create(auth, serverUrl);

        handler.ShouldBeOfType<OAuthDiscoveryHandler>();
        handler.Dispose();
    }

    [Fact]
    public void Create_OAuthWithoutServerUrlOrResource_Throws()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth);

        var ex = Should.Throw<McpLenseAuthException>(() => AuthHandlerFactory.Create(auth));
        ex.Message.ShouldContain("resourceUri");
    }

    [Fact]
    public void Create_OAuthWithExplicitResource_AllowsNullServerUrl()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth, ResourceUri: "https://api.example.com/mcp");

        var handler = AuthHandlerFactory.Create(auth);

        handler.ShouldBeOfType<OAuthDiscoveryHandler>();
        handler.Dispose();
    }

    [Fact]
    public void Create_OAuthMalformedResourceUri_Throws()
    {
        var auth = new ResolvedAuth(AuthKind.OAuth, ResourceUri: "not a uri");

        var ex = Should.Throw<McpLenseAuthException>(() => AuthHandlerFactory.Create(auth));
        ex.Message.ShouldContain("resourceUri");
    }
}
