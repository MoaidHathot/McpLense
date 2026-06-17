using System;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests.Auth;

/// <summary>
/// Exercises the real <see cref="AuthProbe"/> (POST <c>initialize</c> with dual Accept) against live
/// in-process MCP servers - the regression coverage for the GET-probe defect where spec-compliant
/// servers answered 405/406 and the probe gave up Inconclusive.
/// </summary>
public static class AuthProbeIntegrationTests
{
    [Collection("HttpTestServer")]
    public sealed class AgainstAnonymousServer(HttpTestServerFixture fixture)
    {
        [Fact]
        public async Task Probe_AnonymousServer_IsNotInconclusive_AndNotAuthRequired()
        {
            using var probe = new AuthProbe();

            var result = await probe.ProbeAsync(new Uri(fixture.BaseUrl), CancellationToken.None);

            result.Inconclusive.ShouldBeFalse(); // the old GET probe returned 405/406 -> Inconclusive here
            result.RequiresAuth.ShouldBeFalse();
            result.StatusCode.ShouldNotBeNull();
            (result.StatusCode!.Value is >= 200 and < 300).ShouldBeTrue();
        }
    }

    [Collection("BearerHttpTestServer")]
    public sealed class AgainstBearerServer(BearerHttpTestServerFixture fixture)
    {
        [Fact]
        public async Task Probe_BearerGatedServer_SurfacesAuthRequired()
        {
            using var probe = new AuthProbe();

            var result = await probe.ProbeAsync(new Uri(fixture.BaseUrl), CancellationToken.None);

            // The unauthenticated POST initialize is refused with 401, which the probe must surface
            // as RequiresAuth (the old GET probe never reached this verdict for POST-gated servers).
            result.RequiresAuth.ShouldBeTrue();
            result.Inconclusive.ShouldBeFalse();
            result.StatusCode.ShouldBe(401);
        }
    }
}
