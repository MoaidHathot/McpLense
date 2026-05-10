using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace McpLense.E2ETests.Auth;

[Collection("BearerHttpTestServer")]
public class CliBearerE2ETests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly BearerHttpTestServerProcessFixture _fixture;

    public CliBearerE2ETests(BearerHttpTestServerProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Inspect_NoAuth_ReturnsNonZeroExitCode()
    {
        // No auth flag at all => the bearer-required server returns 401 => mcplense reports a server error.
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
    }

    [Fact]
    public async Task Inspect_BearerCorrectTokenLiteral_ReturnsZero()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--auth", "bearer",
            "--auth-token", BearerHttpTestServerProcessFixture.TestToken,
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
        result.StandardOutput.ShouldContain("\"servers\":");
        result.StandardOutput.ShouldContain("\"Echo\"");
    }

    [Fact]
    public async Task Inspect_BearerTokenViaEnvPrefix_ReturnsZero()
    {
        // Use a per-test unique env var name so we don't collide with parallel runs (collection is serialized
        // anyway, but we set/unset the var here cleanly).
        var envName = $"MCPLENSE_E2E_BEARER_TOKEN_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envName, BearerHttpTestServerProcessFixture.TestToken);
        try
        {
            var result = await CliRunner.RunAsync([
                "inspect",
                "--url", _fixture.BaseUrl,
                "--auth", "bearer",
                "--auth-token", $"env:{envName}",
                "--format", "json",
                "--timeout", "30"
            ], DefaultTimeout);

            result.ExitCode.ShouldBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
            result.StandardOutput.ShouldContain("\"Echo\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public async Task Inspect_BearerWrongToken_ReturnsNonZero()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--auth", "bearer",
            "--auth-token", "definitely-not-the-right-token",
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task Call_GetHeader_BearerToken_ReturnsAuthorizationValue()
    {
        var result = await CliRunner.RunAsync([
            "call", "GetHeader",
            "--url", _fixture.BaseUrl,
            "--auth", "bearer",
            "--auth-token", BearerHttpTestServerProcessFixture.TestToken,
            "--args", "{\"name\":\"Authorization\"}",
            "--progress", "false",
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
        result.StandardOutput.ShouldContain($"Bearer {BearerHttpTestServerProcessFixture.TestToken}");
    }

    [Fact]
    public async Task Inspect_AuthOauth_AgainstBearerOnlyServer_ReportsDiscoveryFailure()
    {
        // The bearer-only test server doesn't expose OAuth Protected Resource Metadata, so the
        // OAuthDiscoveryHandler's first hop (PRM fetch) is rejected by the global bearer gate.
        // McpExecutor catches the per-server McpLenseAuthException and surfaces it via the inspect
        // report's Error field (HasErrors=true => exit 1).
        var cacheName = $"e2e-bearer-oauth-stale-{Guid.NewGuid():N}";
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--auth", "oauth",
            "--token-cache-name", cacheName,
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
        var combined = result.StandardOutput + result.StandardError;
        combined.ShouldContain("McpLenseAuthException", Case.Sensitive);
        combined.ShouldContain("Protected Resource Metadata", Case.Sensitive);
    }
}
