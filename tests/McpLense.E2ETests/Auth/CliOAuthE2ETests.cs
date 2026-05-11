using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace McpLense.E2ETests.Auth;

[Collection("OAuthHttpTestServer")]
public class CliOAuthE2ETests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly OAuthHttpTestServerProcessFixture _fixture;

    public CliOAuthE2ETests(OAuthHttpTestServerProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Inspect_NoAuth_AgainstOAuthRequiredServer_ReturnsNonZero()
    {
        // Server returns HTTP 401 + WWW-Authenticate: Bearer ... resource_metadata=... when no token is sent.
        // mcplense should surface this as a per-server failure (HasErrors => exit 1).
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
    }

    [Fact]
    public async Task Inspect_NoAuthFlag_AgainstOAuthRequiredServer_ReturnsNonZero()
    {
        // Same as above but using --no-auth explicitly. The server still rejects with 401.
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--no-auth",
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
    }

    [Fact]
    public async Task Inspect_AuthOauth_AdHoc_NoLongerSupported_ReturnsNonZero()
    {
        // Phase A breaking change: ad-hoc '--auth oauth' is no longer accepted; OAuth (and
        // interactive-browser) auth must come from a profile (--profile <name>).
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--auth", "oauth",
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
        result.StandardError.ShouldContain("--profile");
    }

    [Fact]
    public async Task LoginCommand_NoArgs_FailsWithHint()
    {
        // Phase C: --login/--logout flags are gone; mcplense login is its own top-level command.
        var result = await CliRunner.RunAsync(["login"], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
        result.StandardError.ShouldContain("--all");
        result.StandardError.ShouldContain("--profile");
    }

    [Fact]
    public async Task LogoutCommand_NoArgs_FailsWithHint()
    {
        var result = await CliRunner.RunAsync(["logout"], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
        result.StandardError.ShouldContain("--all");
        result.StandardError.ShouldContain("--profile");
    }

    [Fact]
    public async Task LoginFlag_OnInspect_IsRejected()
    {
        // Phase C: the --login flag was removed.
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--login"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
        result.StandardError.ShouldContain("--login");
    }
}
