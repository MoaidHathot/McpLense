using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.Scanning;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests;

/// <summary>
/// End-to-end integration tests that boot one of the test MCP modes
/// (<see cref="McpLense.TestMcps.Program"/>) in-process and run the full scan pipeline
/// against it. Each test asserts specific check outputs so a regression in any check
/// implementation surfaces here.
/// </summary>
public class TestMcpsScanTests
{
    /// <summary>
    /// Brings up a test MCP in the requested mode, drives <see cref="ScanCommandDispatcher.RunAsync"/>
    /// against its base URL, and returns the produced report. The fixture is per-test so
    /// modes can run in parallel without port collisions.
    /// </summary>
    private static async Task<(ScanReport Report, WebApplication App)> ScanModeAsync(string mode)
    {
        var app = await McpLense.TestMcps.Program.StartAsync(mode);
        var baseUrl = app.Urls.First();
        var target = TargetOptionsFor(baseUrl);
        var report = await ScanCommandDispatcher.RunAsync(
            target,
            handshakeTimeout: TimeSpan.FromSeconds(15),
            cliEnables: null,
            cliDisables: null,
            CancellationToken.None);

        return (report, app);
    }

    private static TargetOptions TargetOptionsFor(string baseUrl)
        => new(
            ConfigPaths: Array.Empty<string>(),
            ServerNames: Array.Empty<string>(),
            ProfilePaths: Array.Empty<string>(),
            DisplayName: null,
            Url: new Uri(baseUrl),
            Transport: TransportPreference.Auto,
            Headers: new System.Collections.Generic.Dictionary<string, string>(),
            Command: null,
            CommandArguments: Array.Empty<string>(),
            WorkingDirectory: null,
            Environment: new System.Collections.Generic.Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

    [Fact]
    public async Task BareMcp_ScansAsAnonymous_AndListsOneTool()
    {
        var (report, app) = await ScanModeAsync("bare");
        await using (app)
        {
            var server = report.Servers.ShouldHaveSingleItem();
            var auth = server.Checks["auth"]!.AsObject();
            auth["classification"]!.GetValue<string>().ShouldBe(AuthClassifications.Anonymous);

            var tools = server.Checks["tools"]!.AsObject();
            tools["fetched"]!.GetValue<bool>().ShouldBeTrue();
            tools["items"]!.AsArray().Count.ShouldBe(1);
            tools["items"]!.AsArray()[0]!["name"]!.GetValue<string>().ShouldBe("Echo");
        }
    }

    [Fact]
    public async Task RichMcp_ExposesInstructions_MissingAnnotations_AndPromptsResources()
    {
        var (report, app) = await ScanModeAsync("rich");
        await using (app)
        {
            var server = report.Servers.ShouldHaveSingleItem();
            var protocol = server.Checks["protocol"]!.AsObject();
            var instr = protocol["instructions"]!.GetValue<string>();
            instr.ShouldContain("Rich Test MCP", Case.Insensitive);
            instr.ShouldContain("https://example.invalid/docs");

            // 'Mystery' tool declared no annotations - all four hints should be in the
            // missingAnnotations list.
            var tools = server.Checks["tools"]!.AsObject()["items"]!.AsArray();
            var mystery = tools.OfType<System.Text.Json.Nodes.JsonObject>()
                .First(t => t["name"]!.GetValue<string>() == "Mystery");
            var missing = mystery["missingAnnotations"]!.AsArray()
                .Select(n => n!.GetValue<string>()).ToHashSet();
            missing.ShouldContain("readOnlyHint");
            missing.ShouldContain("destructiveHint");
            missing.ShouldContain("idempotentHint");
            missing.ShouldContain("openWorldHint");

            // Prompts + resources reachable.
            server.Checks["prompts"]!.AsObject()["fetched"]!.GetValue<bool>().ShouldBeTrue();
        }
    }

    [Fact]
    public async Task LeakyMcp_SurfacesServerHeader_AndStarCors()
    {
        var (report, app) = await ScanModeAsync("leaky");
        await using (app)
        {
            var server = report.Servers.ShouldHaveSingleItem();
            var transport = server.Checks["transport"]!.AsObject();
            var headers = transport["responseHeaders"]!.AsObject();
            headers["server"]!.GetValue<string>().ShouldContain("TestMcp");
            headers["xPoweredBy"]!.GetValue<string>().ShouldBe("leaky-mcp");
            headers["accessControlAllowOrigin"]!.GetValue<string>().ShouldBe("*");
        }
    }

    [Fact]
    public async Task SamplingMcp_ScansBareCleanly_AndIsObservable()
    {
        // The sampling mode advertises sampling-capable instructions but does not actively
        // call back today (server-side sampling-initiated logic would require a richer
        // SDK pattern). This test ensures the mode at least scans cleanly so observers
        // build on it without surprises.
        var (report, app) = await ScanModeAsync("sampling");
        await using (app)
        {
            var server = report.Servers.ShouldHaveSingleItem();
            server.Checks["auth"]!.AsObject()["classification"]!.GetValue<string>().ShouldBe(AuthClassifications.Anonymous);
            server.Checks["serverInfo"]!.AsObject()["title"]!.GetValue<string>().ShouldContain("Sampling");
        }
    }
}
