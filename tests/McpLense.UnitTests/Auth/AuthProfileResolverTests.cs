using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class AuthProfileResolverTests
{
    private static AuthProfile Bearer(string name, string token = "x", int? priority = null)
        => new(name, new ResolvedAuth(AuthKind.Bearer, Token: token), priority);

    private static AuthProfile InteractiveBrowser(string name, IReadOnlyList<string>? scopes = null, int? priority = null)
        => new(name, new ResolvedAuth(
            AuthKind.InteractiveBrowser,
            Scopes: scopes ?? new[] { $"api://{name}/.default" },
            ClientId: "abc",
            CacheName: name), priority);

    private static AuthProfile AzureCli(string name, IReadOnlyList<string>? scopes = null, int? priority = null)
        => new(name, new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: scopes ?? new[] { $"api://{name}/.default" }), priority);

    private static AuthProfile OAuth(string name, IReadOnlyList<string>? scopes = null, int? priority = null)
        => new(name, new ResolvedAuth(
            AuthKind.OAuth,
            Scopes: scopes ?? new[] { $"mcp.{name}.read" },
            CacheName: name), priority);

    private sealed class FakeProbe(AuthProbeResult result) : IAuthProbe
    {
        public int Calls { get; private set; }

        public Task<AuthProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCache(IReadOnlySet<string> cachedNames) : IMsalCacheInspector
    {
        public Task<bool> HasCachedAccountAsync(AuthProfile profile, CancellationToken cancellationToken)
            => Task.FromResult(cachedNames.Contains(profile.Name));
    }

    private static AuthProfileResolver Build(AuthProbeResult? probe = null, IReadOnlySet<string>? cached = null)
        => new(new FakeProbe(probe ?? AuthProbeResult.Empty), new FakeCache(cached ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

    [Fact]
    public async Task ResolveAsync_ExplicitProfile_FoundByName_Wins()
    {
        var resolver = Build();
        var profiles = new[] { InteractiveBrowser("agent365"), InteractiveBrowser("github") };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, "agent365", CancellationToken.None);

        picked!.Name.ShouldBe("agent365");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitProfile_LookupIsCaseInsensitive()
    {
        var resolver = Build();
        var profiles = new[] { InteractiveBrowser("Agent365") };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, "agent365", CancellationToken.None);

        picked!.Name.ShouldBe("Agent365");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitProfile_NotFound_Throws_AndListsAvailable()
    {
        var resolver = Build();
        var profiles = new[] { InteractiveBrowser("agent365"), InteractiveBrowser("github") };

        var ex = await Should.ThrowAsync<UserInputException>(
            () => resolver.ResolveAsync(new Uri("https://x"), profiles, "missing", CancellationToken.None));

        ex.Message.ShouldContain("missing");
        ex.Message.ShouldContain("agent365");
        ex.Message.ShouldContain("github");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitProfile_NotFound_NoProfilesLoaded_MessageMentionsNone()
    {
        var resolver = Build();

        var ex = await Should.ThrowAsync<UserInputException>(
            () => resolver.ResolveAsync(new Uri("https://x"), [], "agent365", CancellationToken.None));

        ex.Message.ShouldContain("(none loaded)");
    }

    [Fact]
    public async Task ResolveAsync_NoExplicitProfile_NoProfiles_Throws_WithSetupHint()
    {
        var resolver = Build();

        var ex = await Should.ThrowAsync<UserInputException>(
            () => resolver.ResolveAsync(new Uri("https://x"), [], requestedProfile: null, CancellationToken.None));

        ex.Message.ShouldContain("McpLense.Profiles.json");
        ex.Message.ShouldContain("--profiles");
        ex.Message.ShouldContain("--no-auth");
    }

    [Fact]
    public async Task ResolveAsync_SingleProfile_NoCache_NoExplicit_StillReturnsIt()
    {
        // With exactly one candidate, the resolver picks it unconditionally - no need to probe
        // or check the cache. The runtime triggers interactive auth on first request if needed.
        var resolver = Build();
        var profiles = new[] { InteractiveBrowser("agent365") };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("agent365");
    }

    [Fact]
    public async Task ResolveAsync_SingleProfile_BypassesProbe()
    {
        // Critical perf / reliability check: with one profile loaded we MUST NOT call the
        // probe. Some servers (Agent365 et al.) are slow or flaky on unauthenticated HEAD
        // probes, and the extra round-trip used to surface as 30+ second hangs.
        var probe = new FakeProbe(AuthProbeResult.Empty);
        var resolver = new AuthProfileResolver(probe, new FakeCache(new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        var profiles = new[] { InteractiveBrowser("agent365") };

        await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        probe.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_MultipleProfiles_OneCached_PicksCached()
    {
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent365" });
        var profiles = new[] { InteractiveBrowser("agent365"), InteractiveBrowser("github") };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("agent365");
    }

    [Fact]
    public async Task ResolveAsync_MultipleProfiles_MultipleCached_Throws_ListsCandidates()
    {
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent365", "github" });
        var profiles = new[] { InteractiveBrowser("agent365"), InteractiveBrowser("github") };

        var ex = await Should.ThrowAsync<UserInputException>(
            () => resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None));

        ex.Message.ShouldContain("Multiple profiles");
        ex.Message.ShouldContain("agent365");
        ex.Message.ShouldContain("github");
        ex.Message.ShouldContain("--profile");
    }

    [Fact]
    public async Task ResolveAsync_MultipleProfiles_NoneCached_Throws_SuggestsLogin()
    {
        var resolver = Build();
        var profiles = new[] { InteractiveBrowser("agent365"), InteractiveBrowser("github") };

        var ex = await Should.ThrowAsync<UserInputException>(
            () => resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None));

        ex.Message.ShouldContain("No cached credentials");
        ex.Message.ShouldContain("mcplense login");
        ex.Message.ShouldContain("agent365");
        ex.Message.ShouldContain("github");
    }

    [Fact]
    public async Task ResolveAsync_ProbeNarrowsByScope_OnlyMatchingProfileSurvives()
    {
        // Probe surfaces "api://agent365/.default" - only the agent365 profile has that scope.
        var probe = new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[] { "api://agent365/.default" });
        var resolver = Build(probe: probe);
        var profiles = new[]
        {
            InteractiveBrowser("agent365", new[] { "api://agent365/.default" }),
            InteractiveBrowser("github",   new[] { "repo", "user" })
        };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("agent365");
    }

    [Fact]
    public async Task ResolveAsync_ProbeScopesUnknown_FallbackToFullCandidateSet()
    {
        // Probe returns scopes that no profile advertises. The narrowing falls back to the full
        // set rather than excluding everything (we don't want stale advertised scopes to brick
        // the resolver entirely).
        var probe = new AuthProbeResult(
            RequiresAuth: true,
            Scopes: new[] { "completely.unrelated.scope" });
        var resolver = Build(probe: probe, cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent365" });
        var profiles = new[]
        {
            InteractiveBrowser("agent365", new[] { "api://agent365/.default" }),
            InteractiveBrowser("github",   new[] { "repo", "user" })
        };

        // With the full set considered and only agent365 cached, agent365 wins.
        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("agent365");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitProfile_BypassesProbe()
    {
        var probe = new FakeProbe(AuthProbeResult.Empty);
        var resolver = new AuthProfileResolver(probe, new FakeCache(new HashSet<string>()));
        var profiles = new[] { InteractiveBrowser("agent365"), InteractiveBrowser("github") };

        await resolver.ResolveAsync(new Uri("https://x"), profiles, "agent365", CancellationToken.None);

        probe.Calls.ShouldBe(0);
    }

    // -------- Auto-mode precedence (kind-based defaults) -------------------------

    [Fact]
    public async Task ResolveAsync_AzureCliAndInteractiveBrowserBothCached_AzureCliWins()
    {
        // The headline precedence rule: silent (azure-cli) beats browser-capable.
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent365-cli", "agent365" });
        var profiles = new[]
        {
            InteractiveBrowser("agent365"),
            AzureCli("agent365-cli")
        };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("agent365-cli");
    }

    [Fact]
    public async Task ResolveAsync_InteractiveBrowserAndOAuthBothCached_InteractiveBrowserWins()
    {
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ib", "oauth" });
        var profiles = new[]
        {
            OAuth("oauth"),
            InteractiveBrowser("ib")
        };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("ib");
    }

    [Fact]
    public async Task ResolveAsync_AzureCliAndOAuthBothCached_AzureCliWins()
    {
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "azcli", "oauth" });
        var profiles = new[]
        {
            OAuth("oauth"),
            AzureCli("azcli")
        };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("azcli");
    }

    [Fact]
    public async Task ResolveAsync_TwoInteractiveBrowserProfilesBothCached_StillErrors()
    {
        // Within-kind ambiguity is a real conflict the user must resolve - precedence
        // doesn't break ties between profiles of the same kind.
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ib-a", "ib-b" });
        var profiles = new[]
        {
            InteractiveBrowser("ib-a"),
            InteractiveBrowser("ib-b")
        };

        var ex = await Should.ThrowAsync<UserInputException>(
            () => resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None));

        ex.Message.ShouldContain("Multiple profiles");
        ex.Message.ShouldContain("ib-a");
        ex.Message.ShouldContain("ib-b");
        ex.Message.ShouldContain("--profile");
    }

    [Fact]
    public async Task ResolveAsync_NoneCached_PrecedencePicksHighestRanked()
    {
        // When no profile has cached credentials, the precedence rule still applies on the
        // uncached candidate set so we don't error out gratuitously when there's a clear winner.
        var resolver = Build();
        var profiles = new[]
        {
            InteractiveBrowser("ib"),
            AzureCli("azcli")
        };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("azcli");
    }

    [Fact]
    public async Task ResolveAsync_NoneCached_TiedAtTopPriority_Throws()
    {
        // Two interactive-browser profiles, neither cached - precedence has nothing to break
        // because both share the same kind-based rank.
        var resolver = Build();
        var profiles = new[]
        {
            InteractiveBrowser("ib-a"),
            InteractiveBrowser("ib-b")
        };

        var ex = await Should.ThrowAsync<UserInputException>(
            () => resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None));

        ex.Message.ShouldContain("tied at priority");
        ex.Message.ShouldContain("ib-a");
        ex.Message.ShouldContain("ib-b");
        ex.Message.ShouldContain("mcplense login");
    }

    // -------- Explicit per-profile priority override ------------------------------

    [Fact]
    public async Task ResolveAsync_ExplicitPriority_OverridesKindDefault()
    {
        // User wants interactive-browser to beat azure-cli for a specific resource. Bumping
        // the interactive-browser profile's priority above 400 (the azure-cli default) does it.
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ib", "azcli" });
        var profiles = new[]
        {
            AzureCli("azcli"),
            InteractiveBrowser("ib", priority: 500)
        };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("ib");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitPriority_BothSetSamePriority_Throws()
    {
        // User explicitly pinned both profiles to the same priority - that's a legit tie.
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ib", "azcli" });
        var profiles = new[]
        {
            AzureCli("azcli", priority: 100),
            InteractiveBrowser("ib", priority: 100)
        };

        var ex = await Should.ThrowAsync<UserInputException>(
            () => resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None));

        ex.Message.ShouldContain("Multiple profiles");
        ex.Message.ShouldContain("azcli");
        ex.Message.ShouldContain("ib");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitPriority_DemotesAzureCliBelowBearer()
    {
        // Edge case: user wants Bearer to beat azure-cli. Set azure-cli priority lower than
        // bearer's default (100).
        var resolver = Build(cached: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bearer", "azcli" });
        var profiles = new[]
        {
            AzureCli("azcli", priority: 50),
            Bearer("bearer")
        };

        var picked = await resolver.ResolveAsync(new Uri("https://x"), profiles, requestedProfile: null, CancellationToken.None);

        picked!.Name.ShouldBe("bearer");
    }

    [Fact]
    public void EffectivePriority_DefaultsByKind()
    {
        AuthProfileResolver.EffectivePriority(AzureCli("a")).ShouldBe(400);
        AuthProfileResolver.EffectivePriority(InteractiveBrowser("a")).ShouldBe(300);
        AuthProfileResolver.EffectivePriority(OAuth("a")).ShouldBe(200);
        AuthProfileResolver.EffectivePriority(Bearer("a")).ShouldBe(100);
    }

    [Fact]
    public void EffectivePriority_ExplicitOverridesDefault()
    {
        AuthProfileResolver.EffectivePriority(AzureCli("a", priority: 50)).ShouldBe(50);
        AuthProfileResolver.EffectivePriority(Bearer("a", priority: 999)).ShouldBe(999);
    }
}
