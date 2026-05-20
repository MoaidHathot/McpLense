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
        SectionResult<PromptInfo>? prompts = null)
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
            ]));

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

    [Fact]
    public void RenderTools_WritesNameAndDescription()
    {
        var console = NewConsole();
        var server = BuildServer();

        TuiApp.RenderTools(console, server);

        var output = console.Output;
        output.ShouldContain("Echo");
        output.ShouldContain("Echoes back");
        output.ShouldContain("Add");
    }

    [Fact]
    public void RenderTools_WithError_WritesErrorMessageAndSkipsTable()
    {
        var console = NewConsole();
        var server = BuildServer(tools: new SectionResult<ToolInfo>(true, [], "tools-listing-failed"));

        TuiApp.RenderTools(console, server);

        var output = console.Output;
        output.ShouldContain("tools-listing-failed");
        output.ShouldNotContain("Description");
    }

    [Fact]
    public void RenderResources_WritesNameUriAndMime()
    {
        var console = NewConsole();
        var server = BuildServer();

        TuiApp.RenderResources(console, server);

        var output = console.Output;
        output.ShouldContain("README");
        output.ShouldContain("file://README.md");
        output.ShouldContain("text/markdown");
    }

    [Fact]
    public void RenderResources_WithError_WritesErrorMessage()
    {
        var console = NewConsole();
        var server = BuildServer(resources: new SectionResult<ResourceInfo>(true, [], "resources-failed"));

        TuiApp.RenderResources(console, server);

        console.Output.ShouldContain("resources-failed");
    }

    [Fact]
    public void RenderResourceTemplates_WritesTemplate()
    {
        var console = NewConsole();
        var server = BuildServer();

        TuiApp.RenderResourceTemplates(console, server);

        var output = console.Output;
        output.ShouldContain("Articles");
        output.ShouldContain("docs://articles/{id}");
    }

    [Fact]
    public void RenderResourceTemplates_WithError_WritesErrorMessage()
    {
        var console = NewConsole();
        var server = BuildServer(resourceTemplates: new SectionResult<ResourceTemplateInfo>(true, [], "rt-failed"));

        TuiApp.RenderResourceTemplates(console, server);

        console.Output.ShouldContain("rt-failed");
    }

    [Fact]
    public void RenderPrompts_WritesArgumentsWithRequiredMarker()
    {
        var console = NewConsole();
        var server = BuildServer();

        TuiApp.RenderPrompts(console, server);

        var output = console.Output;
        output.ShouldContain("CodeReview");
        output.ShouldContain("language*");
        output.ShouldContain("code");
    }

    [Fact]
    public void RenderPrompts_WithError_WritesErrorMessage()
    {
        var console = NewConsole();
        var server = BuildServer(prompts: new SectionResult<PromptInfo>(true, [], "prompts-failed"));

        TuiApp.RenderPrompts(console, server);

        console.Output.ShouldContain("prompts-failed");
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
    public async Task RenderAsync_ExitImmediately_Returns0()
    {
        var console = NewConsole();
        var server = BuildServer();
        var report = new InspectReport(DateTimeOffset.UtcNow, [server]);

        // Server prompt: cursor at index 0 (the server). Down -> Exit, Enter.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
    }

    [Fact]
    public async Task RenderAsync_NavigateBackThenExit_Returns0()
    {
        var console = NewConsole();
        var server = BuildServer();
        var report = new InspectReport(DateTimeOffset.UtcNow, [server]);

        // 1. Server prompt: select first server (Enter at index 0).
        console.Input.PushKey(ConsoleKey.Enter);

        // 2. Section prompt items: Overview, Tools, Resources, Resource Templates, Prompts,
        //    Bookmarks, Back. Down x6 -> Back, Enter.
        for (var i = 0; i < 6; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }
        console.Input.PushKey(ConsoleKey.Enter);

        // 3. Back at server prompt: Down -> Exit, Enter.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
    }
}
