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
    public void Render_Json_AuditReport_LocksWireShape()
    {
        // The audit JSON is a programmatic surface consumed by risk / policy tooling. Lock
        // every section's property names and the stable string identifiers so future shape
        // changes break this test FIRST rather than silently breaking downstream consumers.
        var authScan = new ServerAuthScan(
            Name: "demo",
            Transport: "http",
            Target: "https://example.com/mcp",
            Classification: AuthClassifications.OAuthRfc9728,
            Summary: "OAuth via RFC 9728",
            Details: new AuthScanDetails(StatusCode: 401, ResourceMetadataUrl: "https://example.com/.well-known/oauth-protected-resource"),
            ProfileAttempts: [
                new ProfileAttempt("agent365", "interactive-browser", new[] { "scope/.default" }, true, "ok", null, 22, 0, 0)
            ]);

        var audit = new AuditReport(
            GeneratedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Servers:
            [
                new ServerAudit(
                    Name: "demo",
                    Transport: "http",
                    Target: "https://example.com/mcp",
                    Auth: authScan,
                    ServerInfo: new ServerInfoSummary("server", "Server Title", "1.0.0", null, "https://example.com", null),
                    Protocol: new ProtocolSummary(
                        NegotiatedProtocolVersion: "2025-06-18",
                        Capabilities: new CapabilitiesView(
                            Tools: new ToolsCapabilityView(true),
                            Prompts: null,
                            Resources: new ResourcesCapabilityView(false, true),
                            Logging: new CapabilityFlagView(),
                            Completions: null,
                            Tasks: null,
                            Experimental: null,
                            Extensions: null),
                        Instructions: "You are helpful.",
                        InstructionsLength: 16,
                        Meta: null),
                    Tools: new ToolListing(
                        Fetched: true,
                        FetchedVia: "profile:agent365",
                        FetchError: null,
                        Items: [
                            new ToolEntry(
                                Name: "echo",
                                Title: null,
                                Description: "Echo input",
                                InputSchema: null,
                                OutputSchema: null,
                                Annotations: new ToolAnnotationsView("Echo", true, false, true, false),
                                MissingAnnotations: [],
                                Meta: null)
                        ]),
                    Prompts: new PromptListing(true, "profile:agent365", null, []),
                    Resources: new ResourceListing(true, "profile:agent365", null, [], []),
                    Security: new SecuritySummary(
                        MixedContent: false,
                        Tls: new TlsInfo(
                            Subject: "CN=example.com",
                            Issuer: "CN=Test CA",
                            Thumbprint: "DEADBEEF",
                            SerialNumber: "01",
                            NotBefore: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                            NotAfter: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                            DaysUntilExpiry: 30,
                            SignatureAlgorithm: "sha256ECDSA",
                            SubjectAlternativeNames: new[] { "DNS Name=example.com" },
                            ProtocolVersion: "Tls13"),
                        ResponseHeaders: new ResponseHeadersSummary(
                            Server: "nginx",
                            XPoweredBy: null,
                            StrictTransportSecurity: "max-age=63072000",
                            ContentSecurityPolicy: null,
                            XFrameOptions: null,
                            XContentTypeOptions: "nosniff",
                            ReferrerPolicy: null,
                            AccessControlAllowOrigin: "*",
                            AccessControlAllowCredentials: null,
                            CacheControl: null,
                            Other: new Dictionary<string, string> { ["X-Custom"] = "1" })),
                    OAuth: new OAuthSummary(
                        DcrFromResourceMetadata: new DcrInfo("https://login.example.com/register", null),
                        AuthorizationServers: [
                            new AuthorizationServerInfo(
                                Issuer: "https://login.example.com",
                                Fetched: true,
                                FetchError: null,
                                AuthorizationEndpoint: "https://login.example.com/authorize",
                                TokenEndpoint: "https://login.example.com/token",
                                RegistrationEndpoint: "https://login.example.com/register",
                                IntrospectionEndpoint: null,
                                RevocationEndpoint: null,
                                JwksUri: "https://login.example.com/jwks",
                                ScopesSupported: new[] { "openid" },
                                ResponseTypesSupported: new[] { "code" },
                                GrantTypesSupported: new[] { "authorization_code" },
                                TokenEndpointAuthMethodsSupported: new[] { "none" },
                                CodeChallengeMethodsSupported: new[] { "S256" },
                                ResourceParameterSupported: true,
                                Raw: null)
                        ]),
                    Behavior: new BehaviorProbes(
                        CallNonExistentTool: new CallNonExistentToolProbe(
                            Attempted: true,
                            ToolNameUsed: "__nope__",
                            FetchedVia: "profile:agent365",
                            Outcome: CallNonExistentToolOutcomes.JsonRpcError,
                            JsonRpcErrorCode: -32601,
                            JsonRpcErrorMessage: "Method not found")),
                    Stdio: null)
            ]);

        var output = OutputRenderer.Render(OutputFormat.Json, audit, JsonOptions);

        using var document = JsonDocument.Parse(output);
        var server = document.RootElement.GetProperty("servers")[0];

        // Top-level audit shape
        document.RootElement.GetProperty("generatedAt").GetString().ShouldBe("2026-01-02T03:04:05+00:00");
        server.GetProperty("name").GetString().ShouldBe("demo");
        server.GetProperty("transport").GetString().ShouldBe("http");

        // Auth section is embedded with the same shape as auth-scan.
        var auth = server.GetProperty("auth");
        auth.GetProperty("classification").GetString().ShouldBe("oauth-rfc9728");
        auth.GetProperty("profileAttempts")[0].GetProperty("success").GetBoolean().ShouldBeTrue();

        // ServerInfo
        var info = server.GetProperty("serverInfo");
        info.GetProperty("name").GetString().ShouldBe("server");
        info.GetProperty("version").GetString().ShouldBe("1.0.0");
        info.TryGetProperty("description", out _).ShouldBeFalse();

        // Protocol + capabilities sub-record. Null capabilities are omitted.
        var protocol = server.GetProperty("protocol");
        protocol.GetProperty("negotiatedProtocolVersion").GetString().ShouldBe("2025-06-18");
        protocol.GetProperty("instructions").GetString().ShouldBe("You are helpful.");
        protocol.GetProperty("instructionsLength").GetInt32().ShouldBe(16);
        var caps = protocol.GetProperty("capabilities");
        caps.GetProperty("tools").GetProperty("listChanged").GetBoolean().ShouldBeTrue();
        caps.TryGetProperty("prompts", out _).ShouldBeFalse();
        caps.GetProperty("resources").GetProperty("subscribe").GetBoolean().ShouldBeTrue();
        caps.GetProperty("logging").ValueKind.ShouldBe(JsonValueKind.Object);
        caps.TryGetProperty("completions", out _).ShouldBeFalse();

        // Tools section: declared annotations + missingAnnotations contract
        var tool = server.GetProperty("tools").GetProperty("items")[0];
        tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().ShouldBeTrue();
        tool.GetProperty("missingAnnotations").GetArrayLength().ShouldBe(0);

        // Security: TLS fields preserved with ISO-8601 timestamps
        var security = server.GetProperty("security");
        security.GetProperty("mixedContent").GetBoolean().ShouldBeFalse();
        var tls = security.GetProperty("tls");
        tls.GetProperty("subject").GetString().ShouldBe("CN=example.com");
        tls.GetProperty("daysUntilExpiry").GetInt32().ShouldBe(30);
        tls.GetProperty("protocolVersion").GetString().ShouldBe("Tls13");
        security.GetProperty("responseHeaders").GetProperty("accessControlAllowOrigin").GetString().ShouldBe("*");
        security.GetProperty("responseHeaders").GetProperty("other").GetProperty("X-Custom").GetString().ShouldBe("1");

        // OAuth section with DCR + authorization server entry
        var oauth = server.GetProperty("oauth");
        oauth.GetProperty("dcrFromResourceMetadata").GetProperty("endpoint").GetString().ShouldBe("https://login.example.com/register");
        oauth.GetProperty("authorizationServers")[0].GetProperty("registrationEndpoint").GetString().ShouldBe("https://login.example.com/register");

        // Behaviour probe with the stable outcome string
        var behavior = server.GetProperty("behavior");
        var cn = behavior.GetProperty("callNonExistentTool");
        cn.GetProperty("outcome").GetString().ShouldBe("jsonrpc-error");
        cn.GetProperty("jsonRpcErrorCode").GetInt32().ShouldBe(-32601);

        // Stdio is null/omitted on HTTP entries
        server.TryGetProperty("stdio", out _).ShouldBeFalse();
    }

    [Fact]
    public void CallNonExistentToolOutcomes_AreStableStrings()
    {
        CallNonExistentToolOutcomes.ToolResultReturned.ShouldBe("tool-result-returned");
        CallNonExistentToolOutcomes.JsonRpcError.ShouldBe("jsonrpc-error");
        CallNonExistentToolOutcomes.TransportError.ShouldBe("transport-error");
    }
}
