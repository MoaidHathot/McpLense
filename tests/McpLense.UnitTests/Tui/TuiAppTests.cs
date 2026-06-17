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
        string? error = null)
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
            Error: error);

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
    public async Task RenderAsync_QuitsImmediately_Returns0()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [BuildServer()]);

        console.Input.PushCharacter('q'); // server list -> exit

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
        console.Output.ShouldContain("Select an MCP server");
    }

    [Fact]
    public async Task RenderAsync_EnterSelectsServer_EscBacks_ThenQuits_Returns0()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [BuildServer()]);

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

        console.Input.PushKey(ConsoleKey.Enter);   // select the server
        console.Input.PushCharacter('2');          // sections: Overview=1, Tools=2 -> Tools
        console.Input.PushKey(ConsoleKey.Escape);  // tools list -> back to sections
        console.Input.PushKey(ConsoleKey.Escape);  // sections -> back to servers
        console.Input.PushCharacter('q');          // exit

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
        var server = BuildServer(
            name: "broken",
            transport: "http",
            target: "https://example.test/mcp",
            capabilities: new CapabilitySnapshot(false, false, false, false, false),
            tools: new SectionResult<ToolInfo>(false, []),
            resources: new SectionResult<ResourceInfo>(false, []),
            resourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
            prompts: new SectionResult<PromptInfo>(false, []),
            error: "HttpRequestException: Response status code does not indicate success: 401 (Unauthorized).");
        var report = new InspectReport(DateTimeOffset.UtcNow, [server]);

        console.Input.PushKey(ConsoleKey.Enter);   // select the failed server
        console.Input.PushCharacter('2');          // sections: Tools -> short-circuits to the error notice
        console.Input.PushKey(ConsoleKey.Escape);  // sections -> back to servers
        console.Input.PushCharacter('q');          // exit

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
        var output = console.Output;
        output.ShouldContain("connection failed");  // persistent summary panel
        output.ShouldContain("Connection failed");  // the section's unavailable notice
        output.ShouldContain("Unauthorized");
        output.ShouldContain("unreachable");        // flagged in the server list
    }
}
