using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

public class AuthScannerTests
{
    private sealed class StubProbe : IAuthProbe
    {
        private readonly Dictionary<Uri, AuthProbeResult> _byUrl = new();
        private AuthProbeResult? _default;

        public List<Uri> Probed { get; } = new();

        public void SetDefault(AuthProbeResult result) => _default = result;
        public void SetForUrl(Uri url, AuthProbeResult result) => _byUrl[url] = result;

        public Task<AuthProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken)
        {
            Probed.Add(serverUrl);
            if (_byUrl.TryGetValue(serverUrl, out var hit))
            {
                return Task.FromResult(hit);
            }

            return Task.FromResult(_default ?? AuthProbeResult.Empty);
        }
    }

    private sealed class StubHandshake : IMcpHandshakeProbe
    {
        // Predicate-driven: callers register handlers in the order they want them invoked,
        // and the first matching handler wins. Lets a single test cover "no-auth attempt then
        // each profile attempt" in one fixture without re-mocking between calls.
        private readonly List<(Func<ResolvedServer, bool> Match, Func<ResolvedServer, HandshakeResult> Build)> _handlers = new();

        public List<ResolvedServer> Calls { get; } = new();

        public void OnEvery(Func<ResolvedServer, HandshakeResult> build)
            => _handlers.Add((_ => true, build));

        public void OnAuthKind(AuthKind? kind, Func<ResolvedServer, HandshakeResult> build)
            => _handlers.Add((s => s.Auth?.Kind == kind, build));

        public void OnNoAuth(Func<ResolvedServer, HandshakeResult> build)
            => _handlers.Add((s => s.Auth is null, build));

        public Task<HandshakeResult> TryHandshakeAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(server);

            foreach (var handler in _handlers)
            {
                if (handler.Match(server))
                {
                    return Task.FromResult(handler.Build(server));
                }
            }

            return Task.FromResult(new HandshakeResult(Success: false, Error: "no handler"));
        }
    }

    private static ResolvedServer HttpServer(string name = "example", string url = "https://example.com/mcp")
        => new(
            Name: name,
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

    private static ResolvedServer StdioServer(string name = "everything")
        => new(
            Name: name,
            Kind: ConnectionKind.Stdio,
            Target: "npx -y @modelcontextprotocol/server-everything",
            Source: "config",
            Command: "npx",
            CommandArguments: new[] { "-y", "@modelcontextprotocol/server-everything" },
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: null,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Auth: null);

    private static AuthProfile Profile(string name, AuthKind kind, params string[] scopes)
        => new(
            Name: name,
            Auth: new ResolvedAuth(
                Kind: kind,
                Token: kind == AuthKind.Bearer ? "tok" : null,
                Scopes: scopes.Length == 0 ? null : scopes,
                ClientId: kind == AuthKind.InteractiveBrowser ? "client" : null));

    [Fact]
    public async Task ScanOne_StdioTarget_ReportsStdioClassificationAndNoAttempts()
    {
        var scanner = new AuthScanner(new StubProbe(), new StubHandshake());

        var report = await scanner.ScanCoreAsync(
            new[] { StdioServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers.Count.ShouldBe(1);
        report.Servers[0].Classification.ShouldBe(AuthClassifications.Stdio);
        report.Servers[0].ProfileAttempts.ShouldBeEmpty();
        report.Servers[0].Transport.ShouldBe("stdio");
    }

    [Fact]
    public async Task ScanOne_AnonymousProbe_ConfirmedByHandshake_ReportsAnonymous()
    {
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(StatusCode: 200));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: true, ToolCount: 3, ResourceCount: 0, PromptCount: 1));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var entry = report.Servers[0];
        entry.Classification.ShouldBe(AuthClassifications.Anonymous);
        entry.Details.AnonymousHandshakeSucceeded.ShouldBe(true);
        entry.Details.StatusCode.ShouldBe(200);
        entry.ProfileAttempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ScanOne_AnonymousProbeButHandshakeReturns401_DowngradesToAuthRequired()
    {
        // The HTTP-level GET probe was clean (e.g. server's root path returns 2xx), but the
        // actual MCP initialize handshake fails with what looks like an auth error. The scanner
        // must report 'auth-required-unspecified' so the user knows credentials are needed
        // even though the surface signal said otherwise.
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(StatusCode: 200));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: false, Error: "HttpRequestException: 401 Unauthorized"));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var entry = report.Servers[0];
        entry.Classification.ShouldBe(AuthClassifications.AuthRequiredUnspecified);
        entry.Details.AnonymousHandshakeSucceeded.ShouldBe(false);
        entry.Details.AnonymousHandshakeError!.ShouldContain("401");
    }

    [Fact]
    public async Task ScanOne_AnonymousProbeButHandshakeFailsForOtherReason_StaysUnknown()
    {
        // A handshake failure that doesn't look like auth (transport mismatch, malformed
        // server response, etc.) leaves us at Unknown - we don't want to slap "auth required"
        // on every flaky server.
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(StatusCode: 200));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: false, Error: "Transport closed unexpectedly"));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].Classification.ShouldBe(AuthClassifications.Unknown);
    }

    [Fact]
    public async Task ScanOne_InconclusiveProbeButHandshakeSucceeds_ReportsAnonymous()
    {
        // Context7-style server: HTTP GET probe returns 405 (MCP only accepts POST) so the
        // probe is Inconclusive, but a real MCP `initialize` POST succeeds without
        // credentials. The scanner must now run the no-auth handshake for Inconclusive
        // outcomes too, otherwise we'd never confirm anonymous for the most common MCP
        // server shape (POST-only JSON-RPC endpoints).
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(Inconclusive: true, StatusCode: 405));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: true, ToolCount: 2));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var entry = report.Servers[0];
        entry.Classification.ShouldBe(AuthClassifications.Anonymous);
        entry.Details.AnonymousHandshakeSucceeded.ShouldBe(true);
        entry.Details.StatusCode.ShouldBe(405);
    }

    [Fact]
    public async Task ScanOne_InconclusiveProbeAndHandshakeAlsoFails_StaysUnknown()
    {
        // 405 from the GET + non-auth handshake failure leaves classification Unknown -
        // we won't pretend to know what's wrong. Also asserts that the handshake IS called
        // (no short-circuit on Inconclusive), which is the Context7 fix.
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(Inconclusive: true, StatusCode: 503));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: false, Error: "ConnectionAborted"));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].Classification.ShouldBe(AuthClassifications.Unknown);
        handshake.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ScanOne_InconclusiveProbe_FallsThroughToHandshakeForVerdict()
    {
        // Sanity: after the Context7 fix, Inconclusive no longer short-circuits to Unknown
        // without a follow-up attempt. The scanner now ALWAYS runs the no-auth handshake
        // when the probe didn't surface an explicit challenge, regardless of whether the
        // probe came back Inconclusive or IsEmpty. This guards the invariant that no
        // Inconclusive outcome falls out of ClassifyAsync without going through the
        // handshake first.
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(Inconclusive: true, StatusCode: 503));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: false, Error: "TaskCanceledException: timed out"));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        handshake.Calls.Count.ShouldBe(1);
        report.Servers[0].Classification.ShouldBe(AuthClassifications.Unknown);
    }

    [Fact]
    public async Task ScanOne_InconclusiveProbeAndHandshakeReturns401_ReportsAuthRequired()
    {
        // Inconclusive GET probe + auth-error from the MCP handshake: we now have enough
        // signal to call it AuthRequiredUnspecified instead of leaving Unknown.
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(Inconclusive: true, StatusCode: 405));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: false, Error: "HttpRequestException: 401 Unauthorized"));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].Classification.ShouldBe(AuthClassifications.AuthRequiredUnspecified);
    }

    [Fact]
    public async Task AnonymousClassification_SkipsProfileAttempts()
    {
        // When the server is confirmed anonymous, profile attempts add no signal: any
        // server that just ignores the Authorization header would "succeed" with every
        // profile, which is misleading noise. Skip the loop entirely.
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(StatusCode: 200));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: true, ToolCount: 5));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            new[] { Profile("a", AuthKind.Bearer), Profile("b", AuthKind.AzureCli, "scope/.default") },
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var entry = report.Servers[0];
        entry.Classification.ShouldBe(AuthClassifications.Anonymous);
        entry.ProfileAttempts.ShouldBeEmpty();
        // Exactly one handshake call total: the no-auth confirmation. No profile attempts.
        handshake.Calls.Count.ShouldBe(1);
        handshake.Calls[0].Auth.ShouldBeNull();
    }

    [Fact]
    public async Task AnonymousClassification_FromInconclusiveProbe_AlsoSkipsProfileAttempts()
    {
        // Same skip-profiles invariant applies when anonymous is confirmed via the
        // Inconclusive-then-handshake path (Context7 case).
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(Inconclusive: true, StatusCode: 405));
        var handshake = new StubHandshake();
        handshake.OnNoAuth(_ => new HandshakeResult(Success: true, ToolCount: 2));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            new[] { Profile("a", AuthKind.Bearer), Profile("b", AuthKind.AzureCli, "scope/.default") },
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].Classification.ShouldBe(AuthClassifications.Anonymous);
        report.Servers[0].ProfileAttempts.ShouldBeEmpty();
        handshake.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ScanOne_Rfc9728_ReportsClassificationAndCarriesMetadataDetails()
    {
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(
            RequiresAuth: true,
            ResourceMetadataUrl: "https://example.com/.well-known/oauth-protected-resource",
            Scopes: new[] { "https://example.com/.default", "openid" },
            AuthorizationServers: new[] { "https://login.example.com" },
            Resource: "https://example.com",
            StatusCode: 401,
            WwwAuthenticate: "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\""));

        var scanner = new AuthScanner(probe, new StubHandshake());
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var entry = report.Servers[0];
        entry.Classification.ShouldBe(AuthClassifications.OAuthRfc9728);
        entry.Details.ResourceMetadataUrl.ShouldBe("https://example.com/.well-known/oauth-protected-resource");
        entry.Details.Scopes.ShouldBe(new[] { "https://example.com/.default", "openid" });
        entry.Details.AuthorizationServers.ShouldBe(new[] { "https://login.example.com" });
        entry.Details.Resource.ShouldBe("https://example.com");
        entry.Details.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task ScanOne_BearerWithoutMetadata_ReportsUnannounced()
    {
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(
            RequiresAuth: true,
            StatusCode: 401,
            WwwAuthenticate: "Bearer realm=\"api\""));

        var scanner = new AuthScanner(probe, new StubHandshake());
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].Classification.ShouldBe(AuthClassifications.OAuthBearerUnannounced);
        report.Servers[0].Details.WwwAuthenticate!.ShouldContain("Bearer");
    }

    [Fact]
    public async Task ScanOne_NonBearerChallenge_ReportsUnspecified()
    {
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(
            RequiresAuth: true,
            StatusCode: 401,
            WwwAuthenticate: "Basic realm=\"api\""));

        var scanner = new AuthScanner(probe, new StubHandshake());
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            Array.Empty<AuthProfile>(),
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].Classification.ShouldBe(AuthClassifications.AuthRequiredUnspecified);
    }

    [Fact]
    public async Task Profiles_AreTriedInLoadOrderByDefault()
    {
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(RequiresAuth: true, ResourceMetadataUrl: "https://example.com/prm", Scopes: new[] { "https://example.com/.default" }));

        var handshake = new StubHandshake();
        handshake.OnAuthKind(AuthKind.Bearer, _ => new HandshakeResult(Success: true, ToolCount: 1));
        handshake.OnAuthKind(AuthKind.OAuth, _ => new HandshakeResult(Success: false, Error: "401"));
        handshake.OnAuthKind(AuthKind.InteractiveBrowser, _ => new HandshakeResult(Success: false, Error: "401"));

        var profiles = new[]
        {
            Profile("a-bearer", AuthKind.Bearer),
            Profile("b-oauth", AuthKind.OAuth, "mcp.read"),
            Profile("c-msal", AuthKind.InteractiveBrowser, "scope/.default")
        };

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            profiles,
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var entry = report.Servers[0];
        entry.ProfileAttempts.Select(a => a.ProfileName).ShouldBe(new[] { "a-bearer", "b-oauth", "c-msal" });
        entry.ProfileAttempts[0].Success.ShouldBeTrue();
        entry.ProfileAttempts[0].ToolCount.ShouldBe(1);
        entry.ProfileAttempts[1].Success.ShouldBeFalse();
        entry.ProfileAttempts[2].Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Profiles_SingleSelection_FiltersToNamedProfile()
    {
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(RequiresAuth: true, ResourceMetadataUrl: "https://example.com/prm"));

        var handshake = new StubHandshake();
        handshake.OnEvery(_ => new HandshakeResult(Success: true));

        var scanner = new AuthScanner(probe, handshake);
        var profiles = new[]
        {
            Profile("a", AuthKind.Bearer),
            Profile("b", AuthKind.OAuth, "mcp.read")
        };

        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            profiles,
            new AuthOverrides(Profile: "b"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].ProfileAttempts.Count.ShouldBe(1);
        report.Servers[0].ProfileAttempts[0].ProfileName.ShouldBe("b");
    }

    [Fact]
    public async Task Profiles_UnknownNamedProfile_EmitsErrorAttempt()
    {
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(RequiresAuth: true, ResourceMetadataUrl: "https://example.com/prm"));

        var scanner = new AuthScanner(probe, new StubHandshake());
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            new[] { Profile("a", AuthKind.Bearer) },
            new AuthOverrides(Profile: "missing"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].ProfileAttempts.Count.ShouldBe(1);
        report.Servers[0].ProfileAttempts[0].Success.ShouldBeFalse();
        report.Servers[0].ProfileAttempts[0].Error!.ShouldContain("not found");
    }

    [Fact]
    public async Task Profiles_NoAuthOverride_SkipsAttemptsEntirely()
    {
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(RequiresAuth: true, ResourceMetadataUrl: "https://example.com/prm"));

        var handshake = new StubHandshake();
        handshake.OnEvery(_ => new HandshakeResult(Success: true));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            new[] { Profile("a", AuthKind.Bearer) },
            new AuthOverrides(NoAuth: true),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].ProfileAttempts.ShouldBeEmpty();
        // Critically, the anonymous-confirmation handshake should NOT have run either: the
        // probe already classified the server, and we explicitly opted out of testing
        // anything that requires credentials. (We still avoid an anonymous handshake here
        // because the probe didn't return IsEmpty - it said RequiresAuth=true.)
        handshake.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Profiles_ClassifyOnlyOverride_SkipsAttemptsEntirely()
    {
        // --classify-only is the scan-specific synonym of --no-auth: same end-state, more
        // discoverable name. The scanner must honour both flags equivalently so users can
        // reach the classify-only path via either CLI surface.
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(
            RequiresAuth: true,
            ResourceMetadataUrl: "https://example.com/prm",
            Scopes: new[] { "https://example.com/.default" }));

        var handshake = new StubHandshake();
        handshake.OnEvery(_ => new HandshakeResult(Success: true));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            new[] { Profile("a", AuthKind.Bearer), Profile("b", AuthKind.AzureCli, "scope/.default") },
            new AuthOverrides(ClassifyOnly: true),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var entry = report.Servers[0];
        entry.Classification.ShouldBe(AuthClassifications.OAuthRfc9728);
        // Classification details are still emitted in full so the user gets the RFC 9728
        // signals (the whole point of --classify-only).
        entry.Details.ResourceMetadataUrl.ShouldBe("https://example.com/prm");
        entry.Details.Scopes.ShouldBe(new[] { "https://example.com/.default" });
        // But profile attempts are suppressed, even though both profiles would have
        // 'succeeded' against the stub handshake.
        entry.ProfileAttempts.ShouldBeEmpty();
        handshake.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Profiles_DefaultScope_GetsSubstitutedFromProbe()
    {
        // End-to-end: profile says "<audience>/.default", probe advertises both .default and a
        // specific scope, scanner uses the specific scope when attempting the handshake. This
        // wires AuthScanner up to McpExecutor.MaybeSubstituteScopesFromProbeAsync so a single
        // profile can scan many Entra-protected servers without per-server scope lists.
        var probe = new StubProbe();
        probe.SetDefault(new AuthProbeResult(
            RequiresAuth: true,
            ResourceMetadataUrl: "https://api.example.com/.well-known/oauth-protected-resource",
            Resource: "https://api.example.com",
            Scopes: new[] { "https://api.example.com/.default", "https://api.example.com/User.Read.All" },
            StatusCode: 401));

        var handshake = new StubHandshake();
        handshake.OnEvery(_ => new HandshakeResult(Success: true));

        var scanner = new AuthScanner(probe, handshake);
        var report = await scanner.ScanCoreAsync(
            new[] { HttpServer() },
            new[] { Profile("p", AuthKind.AzureCli, "audience/.default") },
            AuthOverrides.Empty,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        report.Servers[0].ProfileAttempts[0].Scopes.ShouldBe(new[] { "https://api.example.com/User.Read.All" });
    }
}
