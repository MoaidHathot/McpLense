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
    public async Task Inspect_AuthOauth_NoCachedToken_HeadlessFlow_ReportsLoginGuidance()
    {
        // With MCPLENSE_NO_INTERACTIVE_FLOW=1, the OAuthDiscoveryHandler refuses to launch the
        // browser flow and instead throws a McpLenseAuthException pointing the user at --login.
        // McpExecutor catches per-server exceptions and reports them in the inspect report's Error
        // field; HasErrors=true => exit 1.
        var cacheName = $"e2e-no-interactive-{Guid.NewGuid():N}";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = BuildArtifacts.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.Environment["MCPLENSE_NO_INTERACTIVE_FLOW"] = "1";

        var result = await RunWithEnvAsync(psi, [
            "inspect",
            "--url", _fixture.BaseUrl,
            "--auth", "oauth",
            "--token-cache-name", cacheName,
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
        var combined = result.StandardOutput + result.StandardError;
        combined.ShouldContain("--login", Case.Sensitive);
        combined.ShouldContain("MCPLENSE_NO_INTERACTIVE_FLOW", Case.Sensitive);
    }

    [Fact]
    public async Task Logout_NoCachedEntry_ReturnsZero_AndReportsNoEntry()
    {
        // Use a unique cache name so we never collide with an actual cached token on the dev box.
        var cacheName = $"e2e-no-entry-{Guid.NewGuid():N}";

        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--auth", "oauth",
            "--token-cache-name", cacheName,
            "--logout",
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
        result.StandardOutput.ShouldContain("\"action\": \"logout\"");
        result.StandardOutput.ShouldContain("\"success\": true");
        result.StandardOutput.ShouldContain("no cache entry to remove");
    }

    [Fact]
    public async Task Logout_TextFormat_NoCachedEntry_ShowsHumanReadableSummary()
    {
        var cacheName = $"e2e-text-no-entry-{Guid.NewGuid():N}";

        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--auth", "oauth",
            "--token-cache-name", cacheName,
            "--logout",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stdout=<<{result.StandardOutput}>> stderr=<<{result.StandardError}>>");
        result.StandardOutput.ShouldContain("logout: 1/1 succeeded");
        result.StandardOutput.ShouldContain("status: ok");
        result.StandardOutput.ShouldContain("no cache entry to remove");
    }

    [Fact]
    public async Task Login_AndLogout_TogetherFails_AtParseTime()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--auth", "oauth",
            "--login",
            "--logout"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
        result.StandardError.ShouldContain("--login and --logout cannot be combined");
    }

    [Fact]
    public async Task NoAuth_AndLogin_TogetherFails_AtParseTime()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--no-auth",
            "--login"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
        result.StandardError.ShouldContain("--no-auth cannot be combined with --login or --logout");
    }

    [Fact]
    public async Task NoAuth_AndLogout_TogetherFails_AtParseTime()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--no-auth",
            "--logout"
        ], DefaultTimeout);

        result.ExitCode.ShouldNotBe(0);
        result.StandardError.ShouldContain("--no-auth cannot be combined with --login or --logout");
    }

    /// <summary>
    /// Runs <c>mcplense</c> as a subprocess via <c>dotnet exec</c> with a caller-supplied
    /// <see cref="System.Diagnostics.ProcessStartInfo"/> so tests can pre-configure environment
    /// variables (e.g. <c>MCPLENSE_NO_INTERACTIVE_FLOW=1</c>) without polluting the test runner's
    /// own process environment.
    /// </summary>
    private static async Task<CliResult> RunWithEnvAsync(
        System.Diagnostics.ProcessStartInfo psi,
        System.Collections.Generic.IReadOnlyList<string> mcplenseArgs,
        TimeSpan timeout)
    {
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(BuildArtifacts.MainAppDll);
        foreach (var arg in mcplenseArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null) stdout.AppendLine(args.Data);
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) stderr.AppendLine(args.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start mcplense subprocess.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new System.Threading.CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignored */ }
            throw new TimeoutException(
                $"mcplense subprocess did not exit within {timeout}. " +
                $"stdout=<<{stdout}>> stderr=<<{stderr}>>");
        }

        return new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
