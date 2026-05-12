using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class MsalCacheInspectorTests
{
    private static AuthProfile AzureCli(string name) =>
        new(name, new ResolvedAuth(AuthKind.AzureCli, Scopes: new[] { "s/.default" }));

    private static AuthProfile Bearer(string name) =>
        new(name, new ResolvedAuth(AuthKind.Bearer, Token: "t"));

    [Fact]
    public async Task HasCachedAccountAsync_AzureCli_WhenProbeSaysSignedIn_ReturnsTrue()
    {
        var inspector = new MsalCacheInspector(() => true);

        var result = await inspector.HasCachedAccountAsync(AzureCli("p"), CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task HasCachedAccountAsync_AzureCli_WhenProbeSaysSignedOut_ReturnsFalse()
    {
        var inspector = new MsalCacheInspector(() => false);

        var result = await inspector.HasCachedAccountAsync(AzureCli("p"), CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task HasCachedAccountAsync_AzureCli_ProbeIsCachedAcrossCalls()
    {
        // Critical perf invariant: the az-session probe runs at most once per inspector instance,
        // even when many azure-cli profiles share the same inspector during a resolver call.
        var probeCalls = 0;
        var inspector = new MsalCacheInspector(() =>
        {
            probeCalls++;
            return true;
        });

        await inspector.HasCachedAccountAsync(AzureCli("p1"), CancellationToken.None);
        await inspector.HasCachedAccountAsync(AzureCli("p2"), CancellationToken.None);
        await inspector.HasCachedAccountAsync(AzureCli("p3"), CancellationToken.None);

        probeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task HasCachedAccountAsync_Bearer_AlwaysCached()
    {
        var inspector = new MsalCacheInspector(() => false);

        var result = await inspector.HasCachedAccountAsync(Bearer("p"), CancellationToken.None);

        // Bearer profiles have no cache layer; they're always considered ready.
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task HasCachedAccountAsync_AzureCli_DifferentInspectors_ProbeIndependently()
    {
        // Each resolver invocation gets a fresh inspector, so the cache is per-resolve, not
        // process-wide. This keeps long-running processes (TUI, future daemon) responsive to
        // the user running `az login` mid-session.
        var calls1 = 0;
        var calls2 = 0;
        var inspector1 = new MsalCacheInspector(() => { calls1++; return true; });
        var inspector2 = new MsalCacheInspector(() => { calls2++; return true; });

        await inspector1.HasCachedAccountAsync(AzureCli("p"), CancellationToken.None);
        await inspector2.HasCachedAccountAsync(AzureCli("p"), CancellationToken.None);

        calls1.ShouldBe(1);
        calls2.ShouldBe(1);
    }
}
