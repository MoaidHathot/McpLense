using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace McpLense.E2ETests;

/// <summary>
/// Live smoke tests that exercise the shared HTTP client factory against two different public
/// remote MCP server styles, guarding regressions in the session/transport handling that the
/// unit tests can only approximate:
/// <list type="bullet">
///   <item>a POST-only Streamable-HTTP server (CVM triage bridge) - the style that previously lost
///   its session (-32001) when a parallel GET event-stream was opened during enumeration;</item>
///   <item>a FastMCP server (compute-insights lens) - a different SSE/handshake implementation.</item>
/// </list>
/// Disabled by default. Enable with <c>MCPLENSE_PUBLIC_SMOKE=1</c>. Both run anonymously and pin
/// <c>MCPLENSE_NO_PROFILE_AUTO_DISCOVERY=1</c> so a developer's local profiles can't influence the
/// result. They require outbound HTTPS to the two Azure endpoints below.
/// </summary>
public class RemoteServerStyleSmokeTests
{
    private const string PostOnlyStreamableUrl = "https://cvmtriage-mcpbridge.azurewebsites.net";
    private const string FastMcpUrl = "https://compute-insights-assistant.azurewebsites.net/api/mcp/lens/";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    private static readonly IReadOnlyDictionary<string, string> AnonymousEnv =
        new Dictionary<string, string> { ["MCPLENSE_NO_PROFILE_AUTO_DISCOVERY"] = "1" };

    [SkipUnlessEnv("MCPLENSE_PUBLIC_SMOKE")]
    public async Task Inspect_PostOnlyStreamable_EnumeratesWithoutSessionLoss()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", PostOnlyStreamableUrl,
            "--no-auth",
            "--format", "json",
            "--timeout", "90"
        ], AnonymousEnv, DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stderr=<<{result.StandardError}>>");
        // The tool only surfaces if the POST-only session survived the enumeration round-trips.
        result.StandardOutput.ShouldContain("triage_icm_incident");
        result.StandardOutput.ShouldNotContain("-32001");
    }

    [SkipUnlessEnv("MCPLENSE_PUBLIC_SMOKE")]
    public async Task Tools_PostOnlyStreamable_ReportsAnonymousAuth()
    {
        var result = await CliRunner.RunAsync([
            "tools",
            "--url", PostOnlyStreamableUrl,
            "--no-auth",
            "--format", "json",
            "--timeout", "90"
        ], AnonymousEnv, DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stderr=<<{result.StandardError}>>");
        result.StandardOutput.ShouldContain("triage_icm_incident");
        // T1.2: anonymous connections surface their auth status in the report.
        result.StandardOutput.ShouldContain("anonymous");
    }

    [SkipUnlessEnv("MCPLENSE_PUBLIC_SMOKE")]
    public async Task Tools_FastMcp_ListsLensTools()
    {
        var result = await CliRunner.RunAsync([
            "tools",
            "--url", FastMcpUrl,
            "--no-auth",
            "--format", "json",
            "--timeout", "90"
        ], AnonymousEnv, DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stderr=<<{result.StandardError}>>");
        result.StandardOutput.ShouldContain("lens_job_status");
    }
}
