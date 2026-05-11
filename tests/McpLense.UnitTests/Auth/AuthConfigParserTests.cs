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
    public void ParseAuthProfiles_NoBlock_ReturnsEmpty()
    {
        var parser = With(new());
        var root = Parse("""{ }""");

        parser.ParseAuthProfiles(root).Count.ShouldBe(0);
    }

    [Fact]
    public void ParseAuthProfiles_EmptyArray_ReturnsEmpty()
    {
        var parser = With(new());
        var root = Parse("""{ "authProfiles": [] }""");

        parser.ParseAuthProfiles(root).Count.ShouldBe(0);
    }

    [Fact]
    public void ParseAuthProfiles_BearerProfile_IsParsed()
    {
        var parser = With(new() { ["TOK"] = "expanded" });
        var root = Parse("""
        {
          "authProfiles": [
            {
              "name": "github",
              "auth": { "type": "bearer", "token": "${TOK}" }
            }
          ]
        }
        """);

        var profiles = parser.ParseAuthProfiles(root);
        profiles.Count.ShouldBe(1);
        profiles[0].Name.ShouldBe("github");
        profiles[0].Auth.Kind.ShouldBe(AuthKind.Bearer);
        profiles[0].Auth.Token.ShouldBe("expanded");
    }

    [Fact]
    public void ParseAuthProfiles_BearerProfileEnvPrefix_IsExpanded()
    {
        var parser = With(new() { ["TOK"] = "v" });
        var root = Parse("""
        {
          "authProfiles": [
            { "name": "x", "auth": { "type": "bearer", "token": "env:TOK" } }
          ]
        }
        """);

        parser.ParseAuthProfiles(root)[0].Auth.Token.ShouldBe("v");
    }

    [Fact]
    public void ParseAuthProfiles_BearerMissingToken_Throws()
    {
        var parser = With(new());
        var root = Parse("""
        { "authProfiles": [ { "name": "x", "auth": { "type": "bearer" } } ] }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("token");
    }

    [Fact]
    public void ParseAuthProfiles_MissingName_Throws()
    {
        var parser = With(new());
        var root = Parse("""
        { "authProfiles": [ { "auth": { "type": "bearer", "token": "abc" } } ] }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("name");
    }

    [Fact]
    public void ParseAuthProfiles_MissingAuth_Throws()
    {
        var parser = With(new());
        var root = Parse("""
        { "authProfiles": [ { "name": "x" } ] }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("auth");
    }

    [Fact]
    public void ParseAuthProfiles_MissingType_Throws()
    {
        var parser = With(new());
        var root = Parse("""
        { "authProfiles": [ { "name": "x", "auth": { "token": "abc" } } ] }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("type");
    }

    [Fact]
    public void ParseAuthProfiles_UnknownType_MentionsAllSupportedKinds()
    {
        var parser = With(new());
        var root = Parse("""
        { "authProfiles": [ { "name": "x", "auth": { "type": "magic" } } ] }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("bearer");
        ex.Message.ShouldContain("oauth");
        ex.Message.ShouldContain("interactive-browser");
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("Bearer")]
    [InlineData("BEARER")]
    public void ParseAuthProfiles_TypeIsCaseInsensitive(string type)
    {
        var parser = With(new());
        var root = Parse($$"""
        { "authProfiles": [ { "name": "x", "auth": { "type": "{{type}}", "token": "abc" } } ] }
        """);

        parser.ParseAuthProfiles(root)[0].Auth.Kind.ShouldBe(AuthKind.Bearer);
    }

    [Fact]
    public void ParseAuthProfiles_OAuth_AcceptsShape()
    {
        var parser = With(new() { ["URI"] = "http://callback" });
        var root = Parse("""
        {
          "authProfiles": [
            {
              "name": "remote-oauth",
              "auth": {
                "type": "oauth",
                "scopes": ["read", "${MISSING:-write}"],
                "redirectUri": "${URI}",
                "cacheName": "my-cache"
              }
            }
          ]
        }
        """);

        var auth = parser.ParseAuthProfiles(root)[0].Auth;

        auth.Kind.ShouldBe(AuthKind.OAuth);
        auth.Scopes!.ShouldBe(new[] { "read", "write" });
        auth.RedirectUri.ShouldBe("http://callback");
        auth.CacheName.ShouldBe("my-cache");
    }

    [Fact]
    public void ParseAuthProfiles_OAuthDiscoveryAlias_MapsToOAuth()
    {
        var parser = With(new());
        var root = Parse("""
        { "authProfiles": [ { "name": "x", "auth": { "type": "oauthdiscovery" } } ] }
        """);

        parser.ParseAuthProfiles(root)[0].Auth.Kind.ShouldBe(AuthKind.OAuth);
    }

    // -------- InteractiveBrowser (M365 / Entra ID) --------------------------------

    [Fact]
    public void ParseAuthProfiles_InteractiveBrowser_AcceptsShape()
    {
        var parser = With(new() { ["AUD"] = "api://example" });
        var root = Parse("""
        {
          "authProfiles": [
            {
              "name": "agent365",
              "auth": {
                "type": "interactive-browser",
                "clientId": "aebc6443-996d-45c2-90f0-388ff96faa56",
                "tenantId": "common",
                "scopes": ["${AUD}/.default"]
              }
            }
          ]
        }
        """);

        var profile = parser.ParseAuthProfiles(root)[0];
        profile.Name.ShouldBe("agent365");
        profile.Auth.Kind.ShouldBe(AuthKind.InteractiveBrowser);
        profile.Auth.ClientId.ShouldBe("aebc6443-996d-45c2-90f0-388ff96faa56");
        profile.Auth.TenantId.ShouldBe("common");
        profile.Auth.Scopes!.ShouldBe(new[] { "api://example/.default" });
        // cacheName defaults to the profile name when not specified.
        profile.Auth.CacheName.ShouldBe("agent365");
    }

    [Fact]
    public void ParseAuthProfiles_InteractiveBrowser_AliasIsCaseAndDashInsensitive()
    {
        var parser = With(new());
        var root = Parse("""
        {
          "authProfiles": [
            {
              "name": "x",
              "auth": {
                "type": "InteractiveBrowser",
                "clientId": "abc",
                "scopes": ["s/.default"]
              }
            }
          ]
        }
        """);

        parser.ParseAuthProfiles(root)[0].Auth.Kind.ShouldBe(AuthKind.InteractiveBrowser);
    }

    [Fact]
    public void ParseAuthProfiles_InteractiveBrowser_MissingClientId_Throws()
    {
        var parser = With(new());
        var root = Parse("""
        {
          "authProfiles": [
            { "name": "x", "auth": { "type": "interactive-browser", "scopes": ["s/.default"] } }
          ]
        }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("clientId");
    }

    [Fact]
    public void ParseAuthProfiles_InteractiveBrowser_EmptyClientIdAfterExpansion_Throws()
    {
        var parser = With(new() { ["VSCODE_CLIENT_ID"] = string.Empty });
        var root = Parse("""
        {
          "authProfiles": [
            {
              "name": "x",
              "auth": {
                "type": "interactive-browser",
                "clientId": "${VSCODE_CLIENT_ID}",
                "scopes": ["s/.default"]
              }
            }
          ]
        }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("clientId");
    }

    [Fact]
    public void ParseAuthProfiles_InteractiveBrowser_MissingScopes_Throws()
    {
        var parser = With(new());
        var root = Parse("""
        {
          "authProfiles": [
            { "name": "x", "auth": { "type": "interactive-browser", "clientId": "abc" } }
          ]
        }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("scopes");
    }

    [Fact]
    public void ParseAuthProfiles_InteractiveBrowser_EmptyScopesArray_Throws()
    {
        var parser = With(new());
        var root = Parse("""
        {
          "authProfiles": [
            { "name": "x", "auth": { "type": "interactive-browser", "clientId": "abc", "scopes": [] } }
          ]
        }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("scopes");
    }

    [Fact]
    public void ParseAuthProfiles_InteractiveBrowser_TenantIdOptional()
    {
        var parser = With(new());
        var root = Parse("""
        {
          "authProfiles": [
            { "name": "x", "auth": { "type": "interactive-browser", "clientId": "abc", "scopes": ["s/.default"] } }
          ]
        }
        """);

        parser.ParseAuthProfiles(root)[0].Auth.TenantId.ShouldBeNull();
    }

    [Fact]
    public void ParseAuthProfiles_InteractiveBrowser_ExplicitCacheName_Wins()
    {
        var parser = With(new());
        var root = Parse("""
        {
          "authProfiles": [
            {
              "name": "agent365",
              "auth": {
                "type": "interactive-browser",
                "clientId": "abc",
                "scopes": ["s/.default"],
                "cacheName": "mcp-proxy"
              }
            }
          ]
        }
        """);

        parser.ParseAuthProfiles(root)[0].Auth.CacheName.ShouldBe("mcp-proxy");
    }

    [Fact]
    public void ParseAuthProfiles_MultipleProfiles_PreservesOrder()
    {
        var parser = With(new());
        var root = Parse("""
        {
          "authProfiles": [
            { "name": "a", "auth": { "type": "bearer", "token": "1" } },
            { "name": "b", "auth": { "type": "bearer", "token": "2" } },
            { "name": "c", "auth": { "type": "bearer", "token": "3" } }
          ]
        }
        """);

        var profiles = parser.ParseAuthProfiles(root);
        profiles.Select(p => p.Name).ShouldBe(new[] { "a", "b", "c" });
    }

    [Fact]
    public void ParseAuthProfiles_NonObjectArrayEntry_Throws()
    {
        var parser = With(new());
        var root = Parse("""
        { "authProfiles": [ "not-an-object" ] }
        """);

        var ex = Should.Throw<UserInputException>(() => parser.ParseAuthProfiles(root));
        ex.Message.ShouldContain("authProfiles[0]");
    }
}
