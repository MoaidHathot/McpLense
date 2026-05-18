using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

public class McpExecutorScopeSubstitutionTests
{
    private sealed class StubProbe(AuthProbeResult result) : IAuthProbe
    {
        public int Calls { get; private set; }

        public Task<AuthProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static readonly Uri Url = new("https://agent365.svc.cloud.microsoft/agents/tenants/x/servers/mcp_MailTools");

    [Fact]
    public async Task SubstitutesDefaultScopes_WhenProbeAdvertisesOnlyPerResourceDefault()
    {
        // The Agent365 case: profile scope is "<audience>/.default", probe advertises the
        // per-server URL form of the same .default plus standard OIDC scopes. With no specific
        // (non-.default, non-OIDC) scope advertised, McpLense falls back to substituting the
        // .default form so the token request at least targets the correct resource URI.
        var profileAuth = new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: new[] { "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1/.default" });
        var probe = new StubProbe(new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[]
            {
                "https://agent365.svc.cloud.microsoft/agents/tenants/x/servers/mcp_MailTools/.default",
                "openid",
                "profile",
                "offline_access"
            },
            Resource: "https://agent365.svc.cloud.microsoft/agents/tenants/x/servers/mcp_MailTools"));

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[]
        {
            "https://agent365.svc.cloud.microsoft/agents/tenants/x/servers/mcp_MailTools/.default"
        });
    }

    [Fact]
    public async Task PrefersSpecificScopes_OverAdvertisedDefault()
    {
        // When the PRM advertises both ".default" AND specific scope names, the specific names
        // win. Asking for ".default" only emits statically-consented permissions; asking for the
        // specific names triggers dynamic consent (interactive flow) or includes the scope claim
        // in the issued token (azure-cli with prior consent).
        var profileAuth = new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: new[] { "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1/.default" });
        var probe = new StubProbe(new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[]
            {
                "https://api.example.com/.default",
                "https://api.example.com/User.Read.All",
                "https://api.example.com/Mail.Read"
            },
            Resource: "https://api.example.com"));

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[]
        {
            "https://api.example.com/User.Read.All",
            "https://api.example.com/Mail.Read"
        });
    }

    [Fact]
    public async Task QualifiesBareSpecificScopes_UsingMetadataResource()
    {
        // PRM advertises bare scope names (no scheme). McpLense fully-qualifies them with the
        // metadata's "resource" field so the auth server can resolve them to the correct
        // resource server (FQN scopes are what Entra and other OIDC providers expect).
        var profileAuth = new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: new[] { "ea9ffc3e/.default" });
        var probe = new StubProbe(new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[] { "User.Read.All", "Mail.Read" },
            Resource: "https://api.example.com/"));

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[]
        {
            "https://api.example.com/User.Read.All",
            "https://api.example.com/Mail.Read"
        });
    }

    [Fact]
    public async Task FallsBackToServerUrl_WhenMetadataResourceIsMissing()
    {
        // PRM advertises bare scope names but no "resource" field. McpLense still qualifies
        // them, this time using the MCP server URL as the resource base.
        var profileAuth = new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: new[] { "audience/.default" });
        var probe = new StubProbe(new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[] { "User.Read.All" }));

        var serverUrl = new Uri("https://api.example.com/mcp/");
        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, serverUrl, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[] { "https://api.example.com/mcp/User.Read.All" });
    }

    [Fact]
    public async Task SkipsOidcStandardScopes_FromSpecificSet()
    {
        // openid/profile/offline_access/email/etc. describe identity-token claims, not
        // resource-server permissions. They must NEVER win the "specific scope" pass, even
        // though they pass the "non-.default" filter.
        var profileAuth = new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: new[] { "audience/.default" });
        var probe = new StubProbe(new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[] { "openid", "profile", "offline_access", "email" },
            Resource: "https://api.example.com"));

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        // Nothing advertised that we can use - fall through to the original profile scopes.
        substituted.Scopes.ShouldBe(new[] { "audience/.default" });
    }

    [Fact]
    public async Task DeduplicatesSpecificScopes()
    {
        // A misbehaving server that lists the same scope twice (or via a bare + FQN pair that
        // collapses to the same string after qualification) shouldn't produce duplicates in
        // the substituted set - duplicate scopes confuse some auth servers and waste request
        // bytes.
        var profileAuth = new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: new[] { "audience/.default" });
        var probe = new StubProbe(new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[]
            {
                "https://api.example.com/User.Read.All",
                "User.Read.All",
                "https://api.example.com/User.Read.All"
            },
            Resource: "https://api.example.com"));

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[] { "https://api.example.com/User.Read.All" });
    }

    [Fact]
    public async Task KeepsProfileScopes_WhenProbeReturnedNoScopes()
    {
        var profileAuth = new ResolvedAuth(
            AuthKind.InteractiveBrowser,
            Scopes: new[] { "audience/.default" });
        var probe = new StubProbe(new AuthProbeResult(RequiresAuth: true));

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[] { "audience/.default" });
    }

    [Fact]
    public async Task KeepsProfileScopes_WhenProfileUsesExplicitPermissions()
    {
        // Profile has explicit permission names. The user knows what they're asking for; don't
        // override even if PRM advertises something different.
        var profileAuth = new ResolvedAuth(
            AuthKind.OAuth,
            Scopes: new[] { "mcp.read", "mcp.write" });
        var probe = new StubProbe(new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[] { "audience/.default" }));

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[] { "mcp.read", "mcp.write" });
    }

    [Fact]
    public async Task SkipsProbeEntirely_WhenProfileHasMixedScopes()
    {
        // Profile mixes .default with explicit permissions - we don't substitute, AND we don't
        // even probe (the probe-cache lookup wouldn't fire because we short-circuit).
        var profileAuth = new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: new[] { "audience/.default", "mcp.read" });
        var probe = new StubProbe(AuthProbeResult.Empty);

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[] { "audience/.default", "mcp.read" });
        probe.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task SkipsProbe_WhenProfileHasNoScopes()
    {
        var profileAuth = new ResolvedAuth(AuthKind.Bearer, Token: "t");
        var probe = new StubProbe(AuthProbeResult.Empty);

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.ShouldBeSameAs(profileAuth);
        probe.Calls.ShouldBe(0);
    }
}
