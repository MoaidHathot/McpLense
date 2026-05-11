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

    // -------- InteractiveBrowser (M365 / Entra ID) --------------------------------

    [Fact]
    public void Create_InteractiveBrowser_ReturnsInteractiveBrowserHandler()
    {
        var auth = new ResolvedAuth(
            AuthKind.InteractiveBrowser,
            Scopes: new[] { "api://res/.default" },
            ClientId: "aebc6443-996d-45c2-90f0-388ff96faa56");

        var handler = AuthHandlerFactory.Create(auth);

        handler.ShouldBeOfType<InteractiveBrowserHandler>();
        handler.Dispose();
    }

    [Fact]
    public void Create_InteractiveBrowser_MissingClientId_Throws()
    {
        var auth = new ResolvedAuth(AuthKind.InteractiveBrowser, Scopes: new[] { "s" });

        var ex = Should.Throw<McpLenseAuthException>(() => AuthHandlerFactory.Create(auth));
        ex.Message.ShouldContain("clientId");
    }

    [Fact]
    public void Create_InteractiveBrowser_MissingScopes_Throws()
    {
        var auth = new ResolvedAuth(AuthKind.InteractiveBrowser, ClientId: "abc");

        var ex = Should.Throw<McpLenseAuthException>(() => AuthHandlerFactory.Create(auth));
        ex.Message.ShouldContain("scope");
    }

    [Fact]
    public void Create_InteractiveBrowser_MalformedRedirectUri_Throws()
    {
        var auth = new ResolvedAuth(
            AuthKind.InteractiveBrowser,
            Scopes: new[] { "s" },
            ClientId: "abc",
            RedirectUri: "not a uri");

        var ex = Should.Throw<McpLenseAuthException>(() => AuthHandlerFactory.Create(auth));
        ex.Message.ShouldContain("redirectUri");
    }
}
