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
    public async Task SubstitutesDefaultScopes_WhenProbeAdvertisesPerResourceDefault()
    {
        // The Agent365 case: profile scope is "<audience>/.default", probe advertises the
        // per-server URL form of the same .default. McpLense should swap the profile scope for
        // the probe-advertised one so the token request asks for what the server actually wants.
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
            }));

        var substituted = await McpExecutor.MaybeSubstituteScopesFromProbeAsync(profileAuth, Url, probe, CancellationToken.None);

        substituted.Scopes.ShouldBe(new[]
        {
            "https://agent365.svc.cloud.microsoft/agents/tenants/x/servers/mcp_MailTools/.default"
        });
    }

    [Fact]
    public async Task KeepsProfileScopes_WhenProbeHasNoDefaultStyleAdvertised()
    {
        // PRM advertises only granular permissions (no .default) - we should NOT swap, since
        // we don't know how to translate the user's "give me everything you've consented to"
        // intent into a granular permission list.
        var profileAuth = new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: new[] { "audience/.default" });
        var probe = new StubProbe(new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[] { "openid", "profile", "offline_access" }));

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
