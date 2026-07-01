using System;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace McpLense.UnitTests.Tui;

public class TuiAppTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 200;
        return console;
    }

    private static ServerInspection BuildServer(
        string name = "alpha",
        string transport = "stdio",
        string target = "dotnet exec foo.dll",
        CapabilitySnapshot? capabilities = null,
        SectionResult<ToolInfo>? tools = null,
        SectionResult<ResourceInfo>? resources = null,
        SectionResult<ResourceTemplateInfo>? resourceTemplates = null,
        SectionResult<PromptInfo>? prompts = null,
        string? error = null,
        ConnectionAuthInfo? authStatus = null)
        => new(
            Name: name,
            Transport: transport,
            Target: target,
            Capabilities: capabilities ?? new CapabilitySnapshot(true, true, true, false, false),
            Tools: tools ?? new SectionResult<ToolInfo>(true,
            [
                new ToolInfo("Echo", "Echoes back", null),
                new ToolInfo("Add", "Adds two ints", null)
            ]),
            Resources: resources ?? new SectionResult<ResourceInfo>(true,
            [
                new ResourceInfo("README", "file://README.md", "text/markdown", "Project readme")
            ]),
            ResourceTemplates: resourceTemplates ?? new SectionResult<ResourceTemplateInfo>(true,
            [
                new ResourceTemplateInfo("Articles", "docs://articles/{id}", "text/markdown", "Articles")
            ]),
            Prompts: prompts ?? new SectionResult<PromptInfo>(true,
            [
                new PromptInfo("CodeReview", "Reviews code",
                [
                    new PromptArgumentInfo("language", null, true),
                    new PromptArgumentInfo("code", null, false)
                ])
            ]),
            Error: error,
            AuthStatus: authStatus);

    // --- Pure render ---------------------------------------------------

    [Fact]
    public void RenderServerSummary_WritesNameTransportAndTarget()
    {
        var console = NewConsole();
        var server = BuildServer("alpha", "stdio", "dotnet exec foo.dll");

        TuiApp.RenderServerSummary(console, server);

        var output = console.Output;
        output.ShouldContain("alpha");
        output.ShouldContain("stdio server");
        output.ShouldContain("dotnet exec foo.dll");
    }

    [Fact]
    public void RenderServerSummary_WithError_SurfacesConnectionError()
    {
        var console = NewConsole();
        var server = BuildServer(
            "alpha",
            "http",
            "https://example.test/mcp",
            error: "HttpRequestException: Response status code does not indicate success: 401 (Unauthorized).");

        TuiApp.RenderServerSummary(console, server);

        var output = console.Output;
        output.ShouldContain("connection failed");
        output.ShouldContain("Unauthorized");
    }

    [Fact]
    public void RenderServerSummary_AnonymousConnection_ShowsAuthLine()
    {
        var console = NewConsole();
        var server = BuildServer("alpha", "http", "https://example.test/mcp", authStatus: ConnectionAuthInfo.Anonymous);

        TuiApp.RenderServerSummary(console, server);

        console.Output.ShouldContain("auth: anonymous");
    }

    [Fact]
    public void RenderServerSummary_AuthenticatedConnection_ShowsProfile()
    {
        var console = NewConsole();
        var server = BuildServer("alpha", "http", "https://example.test/mcp",
            authStatus: ConnectionAuthInfo.Authenticated("agent365", AuthKind.AzureCli, "auto-pick"));

        TuiApp.RenderServerSummary(console, server);

        var output = console.Output;
        output.ShouldContain("auth: authenticated");
        output.ShouldContain("agent365");
    }

    [Fact]
    public void RenderOverview_WithError_ShowsConnectionFailureAndSkipsTable()
    {
        var console = NewConsole();
        var server = BuildServer(error: "401 (Unauthorized)");

        TuiApp.RenderOverview(console, server);

        var output = console.Output;
        output.ShouldContain("Connection failed");
        output.ShouldContain("Unauthorized");
        // The capabilities/section table must NOT be shown for a failed connection -
        // a server we never reached has no known capabilities to report.
        output.ShouldNotContain("Capabilities");
    }

    [Fact]
    public void RenderOverview_AllSupported_ShowsOkAndCapabilityList()
    {
        var console = NewConsole();
        var server = BuildServer();

        TuiApp.RenderOverview(console, server);

        var output = console.Output;
        output.ShouldContain("Capabilities");
        output.ShouldContain("Tools");
        output.ShouldContain("Resources");
        output.ShouldContain("Resource Templates");
        output.ShouldContain("Prompts");
        output.ShouldContain("ok");
        output.ShouldContain("tools, resources, prompts");
    }

    [Fact]
    public void RenderOverview_ToolsNotSupported_ShowsNotSupported()
    {
        var console = NewConsole();
        var server = BuildServer(tools: new SectionResult<ToolInfo>(false, []));

        TuiApp.RenderOverview(console, server);

        console.Output.ShouldContain("not supported");
    }

    // --- Section counts bar -------------------------------------------

    [Fact]
    public void RenderSectionCountsBar_ShowsCountsAndLabelsForEachSection()
    {
        var console = NewConsole();
        // Default BuildServer: 2 tools, 1 resource, 1 template, 1 prompt.
        var server = BuildServer();

        TuiApp.RenderSectionCountsBar(console, server);

        var output = console.Output;
        output.ShouldContain("2 tools");
        output.ShouldContain("1 prompt");
        output.ShouldContain("1 resource");
        output.ShouldContain("1 template");
    }

    [Fact]
    public void RenderSectionCountsBar_ZeroResources_ShowsZero()
    {
        var console = NewConsole();
        var server = BuildServer(resources: new SectionResult<ResourceInfo>(true, []));

        TuiApp.RenderSectionCountsBar(console, server);

        console.Output.ShouldContain("0 resources");
    }

    [Fact]
    public void RenderSectionCountsBar_IncludesCapabilityChips()
    {
        var console = NewConsole();
        // Default BuildServer capabilities: tools, resources, prompts declared; logging + completions not.
        var server = BuildServer();

        TuiApp.RenderSectionCountsBar(console, server);

        var output = console.Output;
        output.ShouldContain("caps");
        output.ShouldContain("logging");
        output.ShouldContain("completions");
    }

    [Fact]
    public void RenderSectionCountsBar_ConnectionError_ShowsUnreachable_NotCounts()
    {
        var console = NewConsole();
        var server = BuildServer(error: "HttpRequestException: 401 (Unauthorized).");

        TuiApp.RenderSectionCountsBar(console, server);

        var output = console.Output;
        output.ShouldContain("unreachable");
        output.ShouldNotContain("tools");
    }

    // --- Helpers -------------------------------------------------------

    [Fact]
    public void SectionStatus_NoError_Supported_ReturnsOk()
    {
        var section = new SectionResult<ToolInfo>(true, []);
        TuiApp.SectionStatus(section).ShouldBe("ok");
    }

    [Fact]
    public void SectionStatus_NoError_NotSupported_ReturnsNotSupported()
    {
        var section = new SectionResult<ToolInfo>(false, []);
        TuiApp.SectionStatus(section).ShouldBe("not supported");
    }

    [Fact]
    public void SectionStatus_WithError_ReturnsErrorPrefix()
    {
        var section = new SectionResult<ToolInfo>(true, [], "boom");
        TuiApp.SectionStatus(section).ShouldBe("error: boom");
    }

    [Fact]
    public void FormatCapabilities_None_ReturnsLiteralNone()
    {
        var caps = new CapabilitySnapshot(false, false, false, false, false);
        TuiApp.FormatCapabilities(caps).ShouldBe("none");
    }

    [Fact]
    public void FormatCapabilities_All_ReturnsCommaSeparatedOrderedList()
    {
        var caps = new CapabilitySnapshot(true, true, true, true, true);
        TuiApp.FormatCapabilities(caps).ShouldBe("tools, resources, prompts, logging, completions");
    }

    [Fact]
    public void FormatCapabilities_Subset_PreservesDeclarationOrder()
    {
        var caps = new CapabilitySnapshot(false, true, false, true, false);
        TuiApp.FormatCapabilities(caps).ShouldBe("resources, logging");
    }

    // --- Connection failure reason ------------------------------------

    [Theory]
    [InlineData("HttpRequestException: Response status code does not indicate success: 401 (Unauthorized).", "401 Unauthorized")]
    [InlineData("HttpRequestException: Response status code does not indicate success: 403 (Forbidden).", "403 Forbidden")]
    [InlineData("HttpRequestException: Response status code does not indicate success: 404 (Not Found).", "404 Not Found")]
    [InlineData("HttpRequestException: Response status code does not indicate success: 500 (Internal Server Error).", "500 Internal Server Error")]
    public void DescribeConnectionFailure_ExtractsStatusCodeAndReason(string error, string expected)
        => TuiApp.DescribeConnectionFailure(error).ShouldBe(expected);

    [Fact]
    public void DescribeConnectionFailure_BareStatusCode_UsesDefaultPhrase()
        => TuiApp.DescribeConnectionFailure("Server returned 401").ShouldBe("401 Unauthorized");

    [Fact]
    public void DescribeConnectionFailure_DigitsWithoutStatusContext_AreNotTreatedAsCode()
        // A port number (or other address fragment) must not be mistaken for an HTTP status.
        => TuiApp.DescribeConnectionFailure("SocketException: Connection refused (localhost:8403)")
            .ShouldBe("connection refused");

    [Fact]
    public void DescribeConnectionFailure_Timeout_ReportsTimedOut()
        => TuiApp.DescribeConnectionFailure("Timed out (raise --timeout if the operation legitimately needs longer).")
            .ShouldBe("timed out");

    [Fact]
    public void DescribeConnectionFailure_ConnectionRefused_ReportsRefused()
        => TuiApp.DescribeConnectionFailure("SocketException: Connection refused (localhost:9999)")
            .ShouldBe("connection refused");

    [Fact]
    public void DescribeConnectionFailure_UnknownHost_ReportsHostNotFound()
        => TuiApp.DescribeConnectionFailure("HttpRequestException: No such host is known. (bad.invalid:443)")
            .ShouldBe("host not found");

    [Fact]
    public void DescribeConnectionFailure_UnrecognisedException_FallsBackToTypeName()
        => TuiApp.DescribeConnectionFailure("InvalidOperationException: something odd happened")
            .ShouldBe("InvalidOperationException");

    [Fact]
    public void DescribeConnectionFailure_NullOrEmpty_ReturnsNull()
    {
        TuiApp.DescribeConnectionFailure(null).ShouldBeNull();
        TuiApp.DescribeConnectionFailure("   ").ShouldBeNull();
    }

    [Fact]
    public void FormatServerListItem_Reachable_ShowsNameTransportTarget_NoUnreachable()
    {
        var server = BuildServer("alpha", "http", "https://example.test/mcp");
        var item = TuiApp.FormatServerListItem(server);

        item.ShouldContain("alpha");
        item.ShouldContain("http");
        item.ShouldContain("https://example.test/mcp");
        item.ShouldNotContain("unreachable");
    }

    [Fact]
    public void FormatServerListItem_Unreachable_AppendsConciseReason()
    {
        var server = BuildServer("alpha", "http", "https://example.test/mcp",
            error: "HttpRequestException: Response status code does not indicate success: 403 (Forbidden).");
        var item = TuiApp.FormatServerListItem(server);

        item.ShouldContain("unreachable: 403 Forbidden");
    }

    // --- Interactive flow ---------------------------------------------
    //
    // Selection screens are driven by TuiMenu: a number jumps to a row, Enter selects the
    // highlighted row (index 0 by default), Esc backs out, and 'q' exits the top-level list.

    [Fact]
    public async Task RenderAsync_NoServers_Returns1AndShowsRedMessage()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, []);

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(1);
        console.Output.ShouldContain("No servers were resolved.");
    }

    [Fact]
    public async Task RenderAsync_SingleServer_AutoSelects_SkipsSelectionScreen()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [BuildServer("solo")]);

        console.Input.PushCharacter('q'); // section menu -> exit (no server list to go back to)

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
        var output = console.Output;
        // The single server is opened directly: the section menu (and its summary panel) is shown,
        // and the "Select an MCP server" pre-form is skipped entirely.
        output.ShouldContain("Choose a section");
        output.ShouldContain("solo");
        output.ShouldNotContain("Select an MCP server");
    }

    [Fact]
    public async Task RenderAsync_MultipleServers_ShowsSelectionScreen_ThenQuits()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow,
        [
            BuildServer("alpha"),
            BuildServer("bravo")
        ]);

        console.Input.PushCharacter('q'); // server list -> exit

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
        console.Output.ShouldContain("Select an MCP server");
    }

    [Fact]
    public async Task RenderAsync_EnterSelectsServer_EscBacks_ThenQuits_Returns0()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow,
        [
            BuildServer("alpha"),
            BuildServer("bravo")
        ]);

        console.Input.PushKey(ConsoleKey.Enter);   // server list: select highlighted (the server)
        console.Input.PushKey(ConsoleKey.Escape);  // section menu: back to servers
        console.Input.PushCharacter('q');          // server list: exit

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
    }

    [Fact]
    public async Task RenderAsync_NumberSelectsSecondServer()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow,
        [
            BuildServer("alpha"),
            BuildServer("bravo")
        ]);

        console.Input.PushCharacter('2');          // server list: jump to + select the 2nd server
        console.Input.PushKey(ConsoleKey.Escape);  // section menu: back to servers
        console.Input.PushCharacter('q');          // exit

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
        // The summary panel for the selected server proves the number jumped to "bravo".
        console.Output.ShouldContain("bravo");
    }

    [Fact]
    public async Task RenderAsync_DrillIntoTools_ShowsNameAndDescription()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [BuildServer()]);

        // Single server auto-selects, so we land directly on the section menu.
        console.Input.PushCharacter('2');          // sections: Overview=1, Tools=2 -> Tools
        console.Input.PushKey(ConsoleKey.Escape);  // tools list -> back to sections
        console.Input.PushCharacter('q');          // sections -> exit (single server)

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
        var output = console.Output;
        output.ShouldContain("Echo");
        output.ShouldContain("Echoes back"); // the inline description from ToolDisplay
    }

    [Fact]
    public async Task RenderAsync_FailedServer_SurfacesConnectionErrorInSectionAndList()
    {
        var console = NewConsole();
        var broken = BuildServer(
            name: "broken",
            transport: "http",
            target: "https://example.test/mcp",
            capabilities: new CapabilitySnapshot(false, false, false, false, false),
            tools: new SectionResult<ToolInfo>(false, []),
            resources: new SectionResult<ResourceInfo>(false, []),
            resourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
            prompts: new SectionResult<PromptInfo>(false, []),
            error: "HttpRequestException: Response status code does not indicate success: 401 (Unauthorized).");
        // A second (healthy) server keeps the selection list in play so the "unreachable" label is
        // exercised; the broken server is row 1 and auto-selection is therefore not triggered.
        var report = new InspectReport(DateTimeOffset.UtcNow, [broken, BuildServer("healthy")]);

        console.Input.PushKey(ConsoleKey.Enter);   // server list: select the failed server (row 1)
        console.Input.PushCharacter('2');          // sections: Tools -> short-circuits to the error notice
        console.Input.PushKey(ConsoleKey.Escape);  // sections -> back to servers
        console.Input.PushCharacter('q');          // exit

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
        var output = console.Output;
        output.ShouldContain("connection failed");  // persistent summary panel
        output.ShouldContain("Connection failed");  // the section's unavailable notice
        output.ShouldContain("401 Unauthorized");   // the concise, distilled reason
        output.ShouldContain("unreachable: 401 Unauthorized"); // flagged in the server list with the code
    }
}
