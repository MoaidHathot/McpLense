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
    private static AuthProfile Bearer(string name, string token = "x")
        => new(name, new ResolvedAuth(AuthKind.Bearer, Token: token));

    private static AuthProfile InteractiveBrowser(string name, IReadOnlyList<string>? scopes = null)
        => new(name, new ResolvedAuth(
            AuthKind.InteractiveBrowser,
            Scopes: scopes ?? new[] { $"api://{name}/.default" },
            ClientId: "abc",
            CacheName: name));

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
}
