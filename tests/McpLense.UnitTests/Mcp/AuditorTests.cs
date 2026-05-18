using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

public class AuditorTests
{
    // ------- stubs ------------------------------------------------------------------

    private sealed class StubAuthProbe : IAuthProbe
    {
        public AuthProbeResult Result { get; set; } = AuthProbeResult.Empty;
        public Task<AuthProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken)
            => Task.FromResult(Result);
    }

    private sealed class StubHandshake : IMcpHandshakeProbe
    {
        public Func<ResolvedServer, HandshakeResult> Build { get; set; } = _ => new HandshakeResult(Success: true);
        public Task<HandshakeResult> TryHandshakeAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(Build(server));
    }

    private sealed class StubTransportProbe : ITransportProbe
    {
        public TransportProbeResult Result { get; set; } = new TransportProbeResult();
        public List<Uri> Calls { get; } = new();

        public Task<TransportProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken)
        {
            Calls.Add(serverUrl);
            return Task.FromResult(Result);
        }
    }

    private sealed class StubAuthorizationServerProbe : IAuthorizationServerProbe
    {
        public Func<string, AuthorizationServerInfo> Builder { get; set; } =
            issuer => new AuthorizationServerInfo(issuer, true, null, null, null, null, null, null, null, [], [], [], [], [], null, null);

        public List<string> Calls { get; } = new();

        public Task<AuthorizationServerInfo> ProbeAsync(string issuer, CancellationToken cancellationToken)
        {
            Calls.Add(issuer);
            return Task.FromResult(Builder(issuer));
        }
    }

    private sealed class StubSessionInspector : IMcpSessionInspector
    {
        public Func<ResolvedServer, string, InspectionOutcome> Build { get; set; } =
            (_, via) => new InspectionOutcome(true, via, null, null, null, [], [], [], [], null);

        public List<(ResolvedServer Server, string FetchedVia)> Calls { get; } = new();

        public Task<InspectionOutcome> InspectAsync(ResolvedServer server, string fetchedVia, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((server, fetchedVia));
            return Task.FromResult(Build(server, fetchedVia));
        }
    }

    // ------- helpers ----------------------------------------------------------------

    private static ResolvedServer HttpServer(string url = "https://example.com/mcp")
        => new(
            Name: "example",
            Kind: ConnectionKind.Http,
            Target: url,
            Source: "direct-url",
            Command: null,
            CommandArguments: Array.Empty<string>(),
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: new Uri(url),
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Auth: null);

    private static ResolvedServer StdioServer()
        => new(
            Name: "everything",
            Kind: ConnectionKind.Stdio,
            Target: "npx -y @modelcontextprotocol/server-everything",
            Source: "config",
            Command: "npx",
            CommandArguments: new[] { "-y", "@modelcontextprotocol/server-everything" },
            WorkingDirectory: "/tmp",
            Environment: new Dictionary<string, string> { ["FOO"] = "bar" },
            Url: null,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Auth: null);

    private static AuthProfile Profile(string name, AuthKind kind = AuthKind.Bearer, params string[] scopes)
        => new(
            Name: name,
            Auth: new ResolvedAuth(
                Kind: kind,
                Token: kind == AuthKind.Bearer ? "tok" : null,
                Scopes: scopes.Length == 0 ? null : scopes,
                ClientId: kind == AuthKind.InteractiveBrowser ? "client" : null));

    private static Auditor BuildAuditor(
        out StubAuthProbe authProbe,
        out StubHandshake handshake,
        out StubTransportProbe transport,
        out StubAuthorizationServerProbe asProbe,
        out StubSessionInspector inspector)
    {
        authProbe = new StubAuthProbe();
        handshake = new StubHandshake();
        transport = new StubTransportProbe();
        asProbe = new StubAuthorizationServerProbe();
        inspector = new StubSessionInspector();

        var scanner = new AuthScanner(authProbe, handshake);
        return new Auditor(scanner, transport, asProbe, inspector);
    }

    // ------- tests ------------------------------------------------------------------

    [Fact]
    public async Task Audit_StdioTarget_ProducesStdioSummaryAndNoHttpProbes()
    {
        // Stdio servers should surface command / args / cwd / env in the stdio block and
        // skip every HTTP-specific section. The transport probe and inspector must not be
        // called - the user explicitly excluded stdio HTTP-style probing.
        var auditor = BuildAuditor(out _, out _, out var transport, out _, out var inspector);

        var report = await auditor.AuditCoreAsync(
            new[] { StdioServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            checkAuthorizationServers: false,
            handshakeTimeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var server = report.Servers.ShouldHaveSingleItem();
        server.Transport.ShouldBe("stdio");
        server.Stdio.ShouldNotBeNull();
        server.Stdio!.Command.ShouldBe("npx");
        server.Stdio.Arguments.ShouldBe(new[] { "-y", "@modelcontextprotocol/server-everything" });
        server.Stdio.WorkingDirectory.ShouldBe("/tmp");
        server.Stdio.Environment["FOO"].ShouldBe("bar");

        server.ServerInfo.ShouldBeNull();
        server.Protocol.ShouldBeNull();
        server.Tools.Fetched.ShouldBeFalse();
        server.Security.Tls.ShouldBeNull();
        server.OAuth.ShouldBeNull();
        server.Behavior.CallNonExistentTool.ShouldBeNull();

        transport.Calls.ShouldBeEmpty();
        inspector.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Audit_AnonymousHttp_UsesAnonymousInspection_AndSurfacesTransportSecurity()
    {
        // The auth scan finds anonymous (probe clean + handshake ok), so the audit's
        // inspection runs with Auth=null and the FetchedVia is "anonymous". The transport
        // probe runs in parallel and provides the TLS/headers section.
        var auditor = BuildAuditor(out var authProbe, out var handshake, out var transport, out _, out var inspector);
        authProbe.Result = new AuthProbeResult(StatusCode: 200);
        handshake.Build = _ => new HandshakeResult(Success: true);
        transport.Result = new TransportProbeResult(
            StatusCode: 200,
            Reached: true,
            Headers: new ResponseHeadersSummary(
                Server: "nginx",
                XPoweredBy: null,
                StrictTransportSecurity: "max-age=63072000",
                ContentSecurityPolicy: null,
                XFrameOptions: null,
                XContentTypeOptions: "nosniff",
                ReferrerPolicy: null,
                AccessControlAllowOrigin: null,
                AccessControlAllowCredentials: null,
                CacheControl: null,
                Other: new Dictionary<string, string> { ["X-Custom"] = "1" }),
            Tls: new TlsInfo(
                Subject: "CN=example.com",
                Issuer: "CN=Test CA",
                Thumbprint: "ABCDEF",
                SerialNumber: "01",
                NotBefore: DateTimeOffset.UtcNow.AddDays(-30),
                NotAfter: DateTimeOffset.UtcNow.AddDays(60),
                DaysUntilExpiry: 60,
                SignatureAlgorithm: "sha256ECDSA",
                SubjectAlternativeNames: new[] { "DNS Name=example.com" },
                ProtocolVersion: "Tls13"));
        inspector.Build = (server, via) => new InspectionOutcome(
            Success: true,
            FetchedVia: via,
            Error: null,
            ServerInfo: new ServerInfoSummary("test-server", null, "1.0.0", null, null, null),
            Protocol: new ProtocolSummary(
                "2025-06-18",
                new CapabilitiesView(new ToolsCapabilityView(false), null, null, null, null, null, null, null),
                "instructions",
                12,
                null),
            Tools: new[] { new ToolEntry("echo", null, "Echo a string", null, null, null, new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" }) },
            Prompts: [],
            Resources: [],
            Templates: [],
            CallNonExistentTool: new CallNonExistentToolProbe(
                Attempted: true,
                ToolNameUsed: "__nope__",
                FetchedVia: via,
                Outcome: CallNonExistentToolOutcomes.JsonRpcError,
                JsonRpcErrorCode: -32601,
                JsonRpcErrorMessage: "Method not found"));

        var report = await auditor.AuditCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            checkAuthorizationServers: false,
            handshakeTimeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var server = report.Servers.ShouldHaveSingleItem();
        server.Auth.Classification.ShouldBe(AuthClassifications.Anonymous);

        // Inspection ran with FetchedVia="anonymous" and the cloned server had Auth=null.
        inspector.Calls.ShouldHaveSingleItem();
        inspector.Calls[0].FetchedVia.ShouldBe("anonymous");
        inspector.Calls[0].Server.Auth.ShouldBeNull();

        // Tools listing was populated from the inspection result.
        server.Tools.Fetched.ShouldBeTrue();
        server.Tools.FetchedVia.ShouldBe("anonymous");
        server.Tools.Items.ShouldHaveSingleItem().Name.ShouldBe("echo");
        // Missing annotations propagate verbatim - no labelling, just the factual list.
        server.Tools.Items[0].MissingAnnotations.ShouldBe(new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" });

        // Security: TLS + headers + mixedContent false (https://).
        server.Security.MixedContent.ShouldBeFalse();
        server.Security.Tls.ShouldNotBeNull();
        server.Security.Tls!.Subject.ShouldBe("CN=example.com");
        server.Security.ResponseHeaders!.StrictTransportSecurity.ShouldBe("max-age=63072000");
        server.Security.ResponseHeaders.Other["X-Custom"].ShouldBe("1");

        // Behaviour probe surfaced verbatim.
        server.Behavior.CallNonExistentTool.ShouldNotBeNull();
        server.Behavior.CallNonExistentTool!.Outcome.ShouldBe(CallNonExistentToolOutcomes.JsonRpcError);
        server.Behavior.CallNonExistentTool.JsonRpcErrorCode.ShouldBe(-32601);

        // OAuth section is null because the server is anonymous.
        server.OAuth.ShouldBeNull();
    }

    [Fact]
    public async Task Audit_MixedContent_FlagIsTrue_ForHttpUrl()
    {
        // The "mixed content" facet is purely derived from the URL scheme: server is
        // http:// -> mixedContent=true. This is a fact, not a judgement, but it's a
        // signal users explicitly want to read off the report.
        var auditor = BuildAuditor(out var authProbe, out var handshake, out _, out _, out _);
        authProbe.Result = new AuthProbeResult(StatusCode: 200);
        handshake.Build = _ => new HandshakeResult(Success: true);

        var report = await auditor.AuditCoreAsync(
            new[] { HttpServer("http://insecure.example.com/mcp") },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            checkAuthorizationServers: false,
            handshakeTimeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].Security.MixedContent.ShouldBeTrue();
    }

    [Fact]
    public async Task Audit_OAuthRfc9728_WithCheckAuthorizationServers_FetchesEachIssuer()
    {
        // When the auth scan classifies the server as oauth-rfc9728 AND
        // --check-authorization-servers is set, the audit calls the AS probe ONCE per
        // advertised issuer and stores each result on the OAuth section.
        var auditor = BuildAuditor(out var authProbe, out _, out _, out var asProbe, out _);
        authProbe.Result = new AuthProbeResult(
            RequiresAuth: true,
            ResourceMetadataUrl: "https://example.com/.well-known/oauth-protected-resource",
            AuthorizationServers: new[]
            {
                "https://login.example.com",
                "https://other-auth.example.com"
            },
            StatusCode: 401,
            WwwAuthenticate: "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\"");
        asProbe.Builder = issuer => new AuthorizationServerInfo(
            issuer,
            Fetched: true,
            FetchError: null,
            AuthorizationEndpoint: $"{issuer}/authorize",
            TokenEndpoint: $"{issuer}/token",
            RegistrationEndpoint: issuer.Contains("login") ? $"{issuer}/register" : null,
            IntrospectionEndpoint: null,
            RevocationEndpoint: null,
            JwksUri: $"{issuer}/jwks",
            ScopesSupported: new[] { "openid" },
            ResponseTypesSupported: new[] { "code" },
            GrantTypesSupported: new[] { "authorization_code" },
            TokenEndpointAuthMethodsSupported: new[] { "none" },
            CodeChallengeMethodsSupported: new[] { "S256" },
            ResourceParameterSupported: true,
            Raw: null);

        var report = await auditor.AuditCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            checkAuthorizationServers: true,
            handshakeTimeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var oauth = report.Servers[0].OAuth.ShouldNotBeNull();
        oauth.AuthorizationServers.Count.ShouldBe(2);
        asProbe.Calls.ShouldBe(new[] { "https://login.example.com", "https://other-auth.example.com" });

        // DCR endpoint comes from the FIRST authorization server that advertises one.
        oauth.DcrFromResourceMetadata.ShouldNotBeNull();
        oauth.DcrFromResourceMetadata!.Endpoint.ShouldBe("https://login.example.com/register");
    }

    [Fact]
    public async Task Audit_OAuthRfc9728_WithoutCheckAuthorizationServers_SkipsFetchAndEmitsEmptyList()
    {
        // Default behaviour is to NOT fetch authorization-server metadata: surprise outbound
        // calls would surprise air-gapped users. The OAuth block is still emitted (because the
        // server IS oauth-rfc9728), but AuthorizationServers stays empty.
        var auditor = BuildAuditor(out var authProbe, out _, out _, out var asProbe, out _);
        authProbe.Result = new AuthProbeResult(
            RequiresAuth: true,
            ResourceMetadataUrl: "https://example.com/.well-known/oauth-protected-resource",
            AuthorizationServers: new[] { "https://login.example.com" },
            StatusCode: 401,
            WwwAuthenticate: "Bearer");

        var report = await auditor.AuditCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            checkAuthorizationServers: false,
            handshakeTimeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var oauth = report.Servers[0].OAuth.ShouldNotBeNull();
        oauth.AuthorizationServers.ShouldBeEmpty();
        oauth.DcrFromResourceMetadata.ShouldBeNull();
        asProbe.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Audit_RequiresAuth_NoSuccessfulProfile_LeavesEnumerationUnfetched()
    {
        // The auth scan finds RequiresAuth and tries profiles but none succeed: the audit
        // must NOT invent an inspection path. tools/prompts/resources stay fetched=false
        // with a fetchError explaining why.
        var auditor = BuildAuditor(out var authProbe, out var handshake, out _, out _, out var inspector);
        authProbe.Result = new AuthProbeResult(
            RequiresAuth: true,
            ResourceMetadataUrl: "https://example.com/.well-known/oauth-protected-resource",
            Scopes: new[] { "https://example.com/.default" },
            StatusCode: 401);
        // Every profile attempt fails on handshake.
        handshake.Build = _ => new HandshakeResult(Success: false, Error: "HTTP 401");

        var report = await auditor.AuditCoreAsync(
            new[] { HttpServer() },
            new[] { Profile("p1"), Profile("p2") },
            AuthOverrides.Empty,
            checkAuthorizationServers: false,
            handshakeTimeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        // Inspector should NOT have been called at all - there was no usable auth path.
        inspector.Calls.ShouldBeEmpty();

        var server = report.Servers[0];
        server.Tools.Fetched.ShouldBeFalse();
        server.Tools.FetchError.ShouldNotBeNull();
        server.Tools.FetchError!.ShouldContain("No anonymous session");
        server.Prompts.Fetched.ShouldBeFalse();
        server.Resources.Fetched.ShouldBeFalse();
    }

    [Fact]
    public async Task Audit_RequiresAuth_FirstSuccessfulProfile_DrivesInspection()
    {
        // When the auth scan reports several profile attempts and (at least) one succeeded,
        // the audit picks the FIRST successful profile and inspects through it. FetchedVia
        // carries the profile name so downstream consumers know which credential opened the
        // session.
        var auditor = BuildAuditor(out var authProbe, out var handshake, out _, out _, out var inspector);
        authProbe.Result = new AuthProbeResult(
            RequiresAuth: true,
            ResourceMetadataUrl: "https://example.com/.well-known/oauth-protected-resource",
            Scopes: new[] { "https://example.com/.default" },
            StatusCode: 401);
        // p1 fails, p2 succeeds.
        handshake.Build = server => server.Auth?.Token == "tok-p2"
            ? new HandshakeResult(Success: true)
            : new HandshakeResult(Success: false, Error: "401");

        var p1 = new AuthProfile("p1", new ResolvedAuth(AuthKind.Bearer, Token: "tok-p1"));
        var p2 = new AuthProfile("p2", new ResolvedAuth(AuthKind.Bearer, Token: "tok-p2"));

        var report = await auditor.AuditCoreAsync(
            new[] { HttpServer() },
            new[] { p1, p2 },
            AuthOverrides.Empty,
            checkAuthorizationServers: false,
            handshakeTimeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        inspector.Calls.ShouldHaveSingleItem();
        inspector.Calls[0].FetchedVia.ShouldBe("profile:p2");
        inspector.Calls[0].Server.Auth.ShouldNotBeNull();
        inspector.Calls[0].Server.Auth!.Token.ShouldBe("tok-p2");
    }

    [Fact]
    public async Task Audit_AnonymousClassification_ButInspectorReturnsFailure_RecordsFetchError()
    {
        // Defensive case: classification says anonymous but the audit's separate session
        // (which is a longer-lived one) fails. The audit must surface the inspector error
        // on each listing rather than fall back to "tools: count 0 success".
        var auditor = BuildAuditor(out var authProbe, out var handshake, out _, out _, out var inspector);
        authProbe.Result = new AuthProbeResult(StatusCode: 200);
        handshake.Build = _ => new HandshakeResult(Success: true);
        inspector.Build = (_, via) => new InspectionOutcome(
            Success: false,
            FetchedVia: null,
            Error: "transient EOF",
            ServerInfo: null,
            Protocol: null,
            Tools: [],
            Prompts: [],
            Resources: [],
            Templates: [],
            CallNonExistentTool: null);

        var report = await auditor.AuditCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            checkAuthorizationServers: false,
            handshakeTimeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var server = report.Servers[0];
        server.Tools.Fetched.ShouldBeFalse();
        server.Tools.FetchError.ShouldBe("transient EOF");
        server.Prompts.FetchError.ShouldBe("transient EOF");
        server.Resources.FetchError.ShouldBe("transient EOF");
        server.ServerInfo.ShouldBeNull();
        server.Behavior.CallNonExistentTool.ShouldBeNull();
    }
}
