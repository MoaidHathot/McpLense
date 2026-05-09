using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace McpLense.E2ETests;

/// <summary>
/// Live smoke tests against public remote MCP servers (currently context7).
/// Disabled by default. To enable locally or in CI, set the environment variable:
///   MCPLENSE_PUBLIC_SMOKE=1
/// These tests require outbound HTTPS to <see href="https://mcp.context7.com/mcp"/>.
/// </summary>
public class PublicMcpSmokeTests
{
    private const string Context7Url = "https://mcp.context7.com/mcp";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    [SkipUnlessEnv("MCPLENSE_PUBLIC_SMOKE")]
    public async Task Inspect_Context7_ReturnsZeroAndNonEmptyTools()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", Context7Url,
            "--format", "json",
            "--timeout", "60"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stderr=<<{result.StandardError}>>");
        result.StandardOutput.ShouldContain("\"servers\":");
        result.StandardOutput.ShouldContain("\"tools\":");
        // Non-empty tools array.
        result.StandardOutput.ShouldNotContain("\"tools\": []");
    }

    [SkipUnlessEnv("MCPLENSE_PUBLIC_SMOKE")]
    public async Task Tools_Context7_ListsResolveLibraryId()
    {
        var result = await CliRunner.RunAsync([
            "tools",
            "--url", Context7Url,
            "--format", "json",
            "--timeout", "60"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stderr=<<{result.StandardError}>>");
        result.StandardOutput.ShouldContain("resolve-library-id");
    }
}
