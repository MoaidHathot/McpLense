using System;
using System.Text.Json.Nodes;
using McpLense;
using Shouldly;
using Spectre.Console.Testing;
using Xunit;

namespace McpLense.UnitTests.Tui;

public class TuiResultRenderTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 120;
        return console;
    }

    private static ServerReference Server => new("alpha", "stdio", "dotnet exec foo.dll");

    // --- Tool call result -------------------------------------------------

    [Fact]
    public void RenderToolCallResult_TextContent_ShowsText()
    {
        var console = NewConsole();
        var report = new ToolCallReport(
            DateTimeOffset.UnixEpoch, Server, "echo", null, [],
            new CallResultView(false, null, null, [new ContentBlockView("text", Text: "hello world")]));

        TuiApp.RenderToolCallResult(console, report);

        console.Output.ShouldContain("hello world");
    }

    [Fact]
    public void RenderToolCallResult_StructuredContent_RendersJsonValues()
    {
        var console = NewConsole();
        var structured = JsonNode.Parse("""{"temperature":21,"unit":"celsius","ok":true}""");
        var report = new ToolCallReport(
            DateTimeOffset.UnixEpoch, Server, "forecast", null, [],
            new CallResultView(false, structured, null, []));

        TuiApp.RenderToolCallResult(console, report);

        var output = console.Output;
        output.ShouldContain("structured content"); // panel header
        output.ShouldContain("temperature");
        output.ShouldContain("21");
        output.ShouldContain("celsius");
    }

    [Fact]
    public void RenderToolCallResult_JsonStringText_IsUpgradedToJsonPanel()
    {
        var console = NewConsole();
        // A tool that returns a JSON document as a text block should still render highlighted.
        var report = new ToolCallReport(
            DateTimeOffset.UnixEpoch, Server, "raw", null, [],
            new CallResultView(false, null, null,
            [
                new ContentBlockView("text", Text: """{"id":42,"name":"widget"}""", MimeType: "application/json")
            ]));

        TuiApp.RenderToolCallResult(console, report);

        var output = console.Output;
        output.ShouldContain("id");
        output.ShouldContain("42");
        output.ShouldContain("widget");
    }

    [Fact]
    public void RenderToolCallResult_Error_ShowsErrorPanel()
    {
        var console = NewConsole();
        var report = new ToolCallReport(
            DateTimeOffset.UnixEpoch, Server, "boom", null, [], null,
            Error: "HttpRequestException: 500 (Internal Server Error).");

        TuiApp.RenderToolCallResult(console, report);

        var output = console.Output;
        output.ShouldContain("error");
        output.ShouldContain("500 Internal Server Error");
    }

    [Fact]
    public void RenderToolCallResult_Empty_ShowsNoContentNote()
    {
        var console = NewConsole();
        var report = new ToolCallReport(
            DateTimeOffset.UnixEpoch, Server, "quiet", null, [],
            new CallResultView(false, null, null, []));

        TuiApp.RenderToolCallResult(console, report);

        console.Output.ShouldContain("no content returned");
    }

    // --- Read result ------------------------------------------------------

    [Fact]
    public void RenderReadResult_TextContent_ShowsUriAndText()
    {
        var console = NewConsole();
        var report = new ReadReport(
            DateTimeOffset.UnixEpoch, Server, "file://readme", null,
            new ReadResourceView([new ResourceContentView("text", Uri: "file://readme", MimeType: "text/plain", Text: "the contents")]));

        TuiApp.RenderReadResult(console, report);

        var output = console.Output;
        output.ShouldContain("file://readme");
        output.ShouldContain("the contents");
    }

    // --- Prompt result ----------------------------------------------------

    [Fact]
    public void RenderPromptResult_ShowsRoleAndMessageText()
    {
        var console = NewConsole();
        var report = new PromptCallReport(
            DateTimeOffset.UnixEpoch, Server, "greet", null,
            new PromptResultView("a greeting", [new PromptMessageView("assistant", new ContentBlockView("text", Text: "hi there"))]));

        TuiApp.RenderPromptResult(console, report);

        var output = console.Output;
        output.ShouldContain("assistant");
        output.ShouldContain("hi there");
    }

    // --- List detail panels (full, untruncated description) ---------------

    [Fact]
    public void RenderToolListDetail_ShowsFullDescription_NotTruncated()
    {
        var console = NewConsole();
        var longDescription = "This tool does something very elaborate. " + new string('x', 200) + " END-OF-DESC";
        var tool = new ToolInfo("bigtool", longDescription, null);

        TuiApp.RenderToolListDetail(console, tool);

        // The full description (including its very end) must be present - not cropped at 80 chars.
        console.Output.ShouldContain("END-OF-DESC");
    }

    [Fact]
    public void RenderPromptListDetail_ShowsArgsAndDescription()
    {
        var console = NewConsole();
        var prompt = new PromptInfo("review", "Reviews code thoroughly",
        [
            new PromptArgumentInfo("language", null, true),
            new PromptArgumentInfo("code", null, false)
        ]);

        TuiApp.RenderPromptListDetail(console, prompt);

        var output = console.Output;
        output.ShouldContain("Reviews code thoroughly");
        output.ShouldContain("language");
        output.ShouldContain("code");
    }

    [Fact]
    public void RenderResourceListDetail_ShowsUriMimeAndDescription()
    {
        var console = NewConsole();
        var resource = new ResourceInfo("Readme", "file://README.md", "text/markdown", "The project readme file");

        TuiApp.RenderResourceListDetail(console, resource);

        var output = console.Output;
        output.ShouldContain("file://README.md");
        output.ShouldContain("text/markdown");
        output.ShouldContain("The project readme file");
    }
}
