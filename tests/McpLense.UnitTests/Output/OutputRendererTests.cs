using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Output;

public class OutputRendererTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Render_Json_ProducesIndentedJson()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, []);

        var output = OutputRenderer.Render(OutputFormat.Json, report, JsonOptions);

        output.ShouldContain("\"generatedAt\":");
        output.ShouldContain("\"servers\": []");
    }

    [Fact]
    public void Render_Text_DispatchesToTextFormatter()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, [
            new ServerInspection(
                Name: "demo",
                Transport: "stdio",
                Target: "node demo.js",
                Capabilities: new CapabilitySnapshot(true, false, false, false, false),
                Tools: new SectionResult<ToolInfo>(true, [new ToolInfo("echo", "say hi", null)]),
                Resources: new SectionResult<ResourceInfo>(false, []),
                ResourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
                Prompts: new SectionResult<PromptInfo>(false, []))
        ]);

        var output = OutputRenderer.Render(OutputFormat.Text, report, JsonOptions);

        output.ShouldContain("demo [stdio] node demo.js");
        output.ShouldContain("tools: 1");
        output.ShouldContain("- echo: say hi");
    }

    [Fact]
    public void Render_Dumpify_ProducesNonEmptyString()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, []);

        var output = OutputRenderer.Render(OutputFormat.Dumpify, report, JsonOptions);

        output.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Render_TextFallback_ForUnknownPayload_UsesJson()
    {
        var payload = new { name = "x", value = 42 };

        var output = OutputRenderer.Render(OutputFormat.Text, payload, JsonOptions);

        output.ShouldContain("\"name\": \"x\"");
        output.ShouldContain("\"value\": 42");
    }

    [Fact]
    public void Render_Json_AuthScanReport_LocksWireShape()
    {
        // This test guards the scan-command JSON contract that downstream tooling consumes.
        // If you change camelCase property names, classification string constants, the
        // `details` sub-record shape, or null-omission behaviour, this test will fail FIRST
        // so the breakage is caught here rather than in a user's CI.
        var report = new AuthScanReport(
            GeneratedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Servers:
            [
                new ServerAuthScan(
                    Name: "agent365",
                    Transport: "http",
                    Target: "https://agent365.example/mcp",
                    Classification: AuthClassifications.OAuthRfc9728,
                    Summary: "OAuth via RFC 9728 - 1 scope(s) advertised.",
                    Details: new AuthScanDetails(
                        StatusCode: 401,
                        WwwAuthenticate: "Bearer resource_metadata=\"https://agent365.example/.well-known/oauth-protected-resource\"",
                        ResourceMetadataUrl: "https://agent365.example/.well-known/oauth-protected-resource",
                        Resource: "https://agent365.example",
                        Scopes: new[] { "https://agent365.example/.default" },
                        AuthorizationServers: new[] { "https://login.example.com" }),
                    ProfileAttempts:
                    [
                        new ProfileAttempt(
                            ProfileName: "agent365",
                            AuthKind: "interactive-browser",
                            Scopes: new[] { "https://agent365.example/.default" },
                            Success: true,
                            Detail: "Handshake succeeded: 22 tool(s).",
                            ToolCount: 22)
                    ])
            ]);

        var output = OutputRenderer.Render(OutputFormat.Json, report, JsonOptions);

        // Round-trip parse: anything documented as part of the wire shape must survive a
        // JsonDocument parse without exceptions. This also guarantees the output is valid
        // JSON (the StringWriter could in principle emit malformed JSON if a custom
        // converter regressed).
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        // Top-level shape.
        root.GetProperty("generatedAt").GetString().ShouldBe("2026-01-02T03:04:05+00:00");
        var servers = root.GetProperty("servers");
        servers.GetArrayLength().ShouldBe(1);

        // Per-server shape: camelCase property names, classification is the stable string
        // constant (NOT a re-encoded enum), transport is the lowercase string ("http").
        var server = servers[0];
        server.GetProperty("name").GetString().ShouldBe("agent365");
        server.GetProperty("transport").GetString().ShouldBe("http");
        server.GetProperty("target").GetString().ShouldBe("https://agent365.example/mcp");
        server.GetProperty("classification").GetString().ShouldBe("oauth-rfc9728");
        server.GetProperty("summary").GetString()!.ShouldContain("RFC 9728");

        // Details sub-object: every documented field appears with the documented camelCase
        // name. Null/missing fields are omitted entirely (no "scopes": null noise) - this is
        // critical for keeping the payload small on the anonymous-server case.
        var details = server.GetProperty("details");
        details.GetProperty("statusCode").GetInt32().ShouldBe(401);
        details.GetProperty("wwwAuthenticate").GetString()!.ShouldContain("Bearer");
        details.GetProperty("resourceMetadataUrl").GetString().ShouldBe("https://agent365.example/.well-known/oauth-protected-resource");
        details.GetProperty("resource").GetString().ShouldBe("https://agent365.example");
        details.GetProperty("scopes")[0].GetString().ShouldBe("https://agent365.example/.default");
        details.GetProperty("authorizationServers")[0].GetString().ShouldBe("https://login.example.com");
        details.TryGetProperty("anonymousHandshakeSucceeded", out _).ShouldBeFalse();
        details.TryGetProperty("probeError", out _).ShouldBeFalse();

        // Profile attempt sub-object: success path. Counts that are null on the record stay
        // out of the JSON entirely so consumers can use `if "toolCount" in attempt` semantics.
        var attempts = server.GetProperty("profileAttempts");
        attempts.GetArrayLength().ShouldBe(1);
        var attempt = attempts[0];
        attempt.GetProperty("profileName").GetString().ShouldBe("agent365");
        attempt.GetProperty("authKind").GetString().ShouldBe("interactive-browser");
        attempt.GetProperty("success").GetBoolean().ShouldBeTrue();
        attempt.GetProperty("detail").GetString()!.ShouldContain("22");
        attempt.GetProperty("toolCount").GetInt32().ShouldBe(22);
        attempt.TryGetProperty("error", out _).ShouldBeFalse();
        attempt.TryGetProperty("resourceCount", out _).ShouldBeFalse();
        attempt.TryGetProperty("promptCount", out _).ShouldBeFalse();
    }

    [Fact]
    public void Render_Json_AuthScanReport_AnonymousCase_OmitsAllNulls()
    {
        // Companion to the OAuth case: when the server is anonymous, the report has tiny
        // details (status + anonymousHandshakeSucceeded). The JSON must NOT include the
        // OAuth-only fields as "null" - omission is the contract for consumers writing
        // pattern matches like `if "resourceMetadataUrl" in details`.
        var report = new AuthScanReport(
            GeneratedAt: DateTimeOffset.UnixEpoch,
            Servers:
            [
                new ServerAuthScan(
                    Name: "context7",
                    Transport: "http",
                    Target: "https://mcp.context7.com/mcp",
                    Classification: AuthClassifications.Anonymous,
                    Summary: "Server accepts unauthenticated MCP sessions.",
                    Details: new AuthScanDetails(
                        StatusCode: 405,
                        AnonymousHandshakeSucceeded: true),
                    ProfileAttempts: [])
            ]);

        var output = OutputRenderer.Render(OutputFormat.Json, report, JsonOptions);

        using var document = JsonDocument.Parse(output);
        var details = document.RootElement
            .GetProperty("servers")[0]
            .GetProperty("details");

        details.GetProperty("statusCode").GetInt32().ShouldBe(405);
        details.GetProperty("anonymousHandshakeSucceeded").GetBoolean().ShouldBeTrue();

        // No OAuth-only noise.
        details.TryGetProperty("wwwAuthenticate", out _).ShouldBeFalse();
        details.TryGetProperty("resourceMetadataUrl", out _).ShouldBeFalse();
        details.TryGetProperty("resource", out _).ShouldBeFalse();
        details.TryGetProperty("scopes", out _).ShouldBeFalse();
        details.TryGetProperty("authorizationServers", out _).ShouldBeFalse();
        details.TryGetProperty("probeError", out _).ShouldBeFalse();
    }

    [Fact]
    public void Render_Json_AuthScanReport_ClassificationConstants_AreStable()
    {
        // Belt-and-braces: snapshot the six classification strings the rest of the codebase
        // documents as stable wire identifiers. If anyone touches AuthClassifications, this
        // test forces them to think about the consumer-visible impact.
        AuthClassifications.Stdio.ShouldBe("stdio");
        AuthClassifications.Anonymous.ShouldBe("anonymous");
        AuthClassifications.OAuthRfc9728.ShouldBe("oauth-rfc9728");
        AuthClassifications.OAuthBearerUnannounced.ShouldBe("oauth-bearer-unannounced");
        AuthClassifications.AuthRequiredUnspecified.ShouldBe("auth-required-unspecified");
        AuthClassifications.Unknown.ShouldBe("unknown");
    }


    [Fact]
    public void CallNonExistentToolOutcomes_AreStableStrings()
    {
        CallNonExistentToolOutcomes.ToolResultReturned.ShouldBe("tool-result-returned");
        CallNonExistentToolOutcomes.JsonRpcError.ShouldBe("jsonrpc-error");
        CallNonExistentToolOutcomes.TransportError.ShouldBe("transport-error");
    }
}