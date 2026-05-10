using System.Collections.Generic;
using System.Text.Json.Nodes;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class AuthConfigParserTests
{
    private static AuthConfigParser With(Dictionary<string, string?> env)
    {
        var expander = new EnvironmentExpander(name => env.TryGetValue(name, out var value) ? value : null);
        return new AuthConfigParser(expander);
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Parse_NoAuthBlock_ReturnsNull()
    {
        var parser = With(new());
        var server = Parse("""{ "url": "https://x" }""");

        var auth = parser.Parse(server, "x", new Dictionary<string, string>());

        auth.ShouldBeNull();
    }

    [Fact]
    public void Parse_BearerWithLiteralToken_Returns()
    {
        var parser = With(new());
        var server = Parse("""
        { "url": "https://x", "auth": { "type": "bearer", "token": "abc" } }
        """);

        var auth = parser.Parse(server, "x", new Dictionary<string, string>());

        auth.ShouldNotBeNull();
        auth.Kind.ShouldBe(AuthKind.Bearer);
        auth.Token.ShouldBe("abc");
    }

    [Fact]
    public void Parse_BearerExpandsToken()
    {
        var parser = With(new() { ["TOK"] = "expanded" });
        var server = Parse("""
        { "url": "https://x", "auth": { "type": "bearer", "token": "${TOK}" } }
        """);

        var auth = parser.Parse(server, "x", new Dictionary<string, string>());

        auth!.Token.ShouldBe("expanded");
    }

    [Fact]
    public void Parse_BearerExpandsTokenViaEnvPrefix()
    {
        var parser = With(new() { ["TOK"] = "v" });
        var server = Parse("""
        { "url": "https://x", "auth": { "type": "bearer", "token": "env:TOK" } }
        """);

        parser.Parse(server, "x", new Dictionary<string, string>())!.Token.ShouldBe("v");
    }

    [Fact]
    public void Parse_BearerMissingToken_Throws()
    {
        var parser = With(new());
        var server = Parse("""{ "url": "https://x", "auth": { "type": "bearer" } }""");

        var ex = Should.Throw<UserInputException>(() => parser.Parse(server, "x", new Dictionary<string, string>()));
        ex.Message.ShouldContain("servers.x.auth.token");
    }

    [Fact]
    public void Parse_BearerEmptyTokenAfterExpansion_Throws()
    {
        var parser = With(new() { ["TOK"] = string.Empty });
        var server = Parse("""
        { "url": "https://x", "auth": { "type": "bearer", "token": "${TOK}" } }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.Parse(server, "x", new Dictionary<string, string>()));
        ex.Message.ShouldContain("token");
    }

    [Fact]
    public void Parse_AuthBlockWithExplicitAuthorizationHeader_Throws()
    {
        var parser = With(new());
        var server = Parse("""
        { "url": "https://x", "auth": { "type": "bearer", "token": "abc" } }
        """);
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer y" };

        var ex = Should.Throw<UserInputException>(() => parser.Parse(server, "x", headers));
        ex.Message.ShouldContain("cannot set both");
        ex.Message.ShouldContain("'auth' block");
    }

    [Fact]
    public void Parse_TypeMissing_Throws()
    {
        var parser = With(new());
        var server = Parse("""{ "url": "https://x", "auth": { "token": "abc" } }""");

        var ex = Should.Throw<UserInputException>(() => parser.Parse(server, "x", new Dictionary<string, string>()));
        ex.Message.ShouldContain("auth.type is required");
    }

    [Fact]
    public void Parse_UnknownType_Throws()
    {
        var parser = With(new());
        var server = Parse("""{ "url": "https://x", "auth": { "type": "magic" } }""");

        var ex = Should.Throw<UserInputException>(() => parser.Parse(server, "x", new Dictionary<string, string>()));
        ex.Message.ShouldContain("not recognised");
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("Bearer")]
    [InlineData("BEARER")]
    public void Parse_TypeIsCaseInsensitive(string type)
    {
        var parser = With(new());
        var server = Parse($$"""
        { "url": "https://x", "auth": { "type": "{{type}}", "token": "abc" } }
        """);

        parser.Parse(server, "x", new Dictionary<string, string>())!.Kind.ShouldBe(AuthKind.Bearer);
    }

    [Fact]
    public void Parse_OAuth_AcceptsShape()
    {
        var parser = With(new() { ["URI"] = "http://callback" });
        var server = Parse("""
        {
          "url": "https://x",
          "auth": {
            "type": "oauth",
            "scopes": ["read", "${MISSING:-write}"],
            "redirectUri": "${URI}",
            "cacheName": "my-cache"
          }
        }
        """);

        var auth = parser.Parse(server, "x", new Dictionary<string, string>());

        auth.ShouldNotBeNull();
        auth.Kind.ShouldBe(AuthKind.OAuth);
        auth.Scopes!.ShouldBe(new[] { "read", "write" });
        auth.RedirectUri.ShouldBe("http://callback");
        auth.CacheName.ShouldBe("my-cache");
    }

    [Fact]
    public void Parse_OAuthDiscoveryAlias_MapsToOAuth()
    {
        var parser = With(new());
        var server = Parse("""
        { "url": "https://x", "auth": { "type": "oauthdiscovery" } }
        """);

        parser.Parse(server, "x", new Dictionary<string, string>())!.Kind.ShouldBe(AuthKind.OAuth);
    }
}
