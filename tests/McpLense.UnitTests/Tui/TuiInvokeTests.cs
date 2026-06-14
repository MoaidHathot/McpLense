using System.Text.Json.Nodes;
using McpLense;
using ModelContextProtocol;
using Shouldly;
using Spectre.Console.Testing;
using Xunit;

namespace McpLense.UnitTests.Tui;

/// <summary>
/// Drives the interactive-invocation path of the TUI end to end with a scripted console and a
/// recording <see cref="IMcpSession"/> fake (opened via a fake connector), so no transport is
/// opened. Proves that selecting a tool and choosing "Call tool" dispatches over the session.
/// </summary>
public class TuiInvokeTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 200;
        return console;
    }

    private sealed class RecordingSession : IMcpSession
    {
        public string? ToolName;
        public JsonObject? Arguments;

        public ServerReference Server { get; } = new("alpha", "stdio", "dotnet exec foo.dll");

        public Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ToolInfo>>([]);

        public Task<IReadOnlyList<PromptInfo>> ListPromptsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PromptInfo>>([]);

        public Task<ToolCallReport> CallToolAsync(string toolName, JsonObject arguments, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
        {
            ToolName = toolName;
            Arguments = arguments;
            var report = new ToolCallReport(
                DateTimeOffset.UnixEpoch, Server, toolName, arguments, [],
                new CallResultView(IsError: false, StructuredContent: null, Meta: null,
                    Content: [new ContentBlockView("text", Text: "RESULT-OK")]));
            return Task.FromResult(report);
        }

        public Task<ReadReport> ReadResourceAsync(string resourceOrTemplate, JsonObject? arguments, CancellationToken cancellationToken)
            => Task.FromResult(new ReadReport(DateTimeOffset.UnixEpoch, Server, resourceOrTemplate, arguments, new ReadResourceView([])));

        public Task<PromptCallReport> GetPromptAsync(string promptName, JsonObject arguments, CancellationToken cancellationToken)
            => Task.FromResult(new PromptCallReport(DateTimeOffset.UnixEpoch, Server, promptName, arguments, new PromptResultView(null, [])));

        public Task<IReadOnlyList<string>> CompletePromptArgumentAsync(string promptName, string argumentName, string partialValue, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> CompleteTemplateArgumentAsync(string uriTemplate, string argumentName, string partialValue, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ServerInspection SingleToolServer()
        => new(
            Name: "alpha",
            Transport: "stdio",
            Target: "dotnet exec foo.dll",
            Capabilities: new CapabilitySnapshot(true, false, false, false, false),
            Tools: new SectionResult<ToolInfo>(true, [new ToolInfo("Ping", "no-arg tool", null)]),
            Resources: new SectionResult<ResourceInfo>(false, []),
            ResourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
            Prompts: new SectionResult<PromptInfo>(false, []));

    [Fact]
    public async Task CallTool_NoArgs_InvokesSessionAndShowsResult()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [SingleToolServer()]);
        var session = new RecordingSession();
        McpSessionConnector connector = (_, _) => Task.FromResult<IMcpSession>(session);

        // Server select -> alpha.
        console.Input.PushKey(ConsoleKey.Enter);
        // Section menu -> Tools (index 1).
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Tools list ([Search], [Back], Ping) -> Ping.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Tool actions (Call tool, Bookmark, Back) -> Call tool.
        console.Input.PushKey(ConsoleKey.Enter);
        // Confirm "Run now?" -> accept default (yes).
        console.Input.PushKey(ConsoleKey.Enter);
        // Tool actions again -> Back.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Tools list -> [Back].
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Section menu -> Back.
        for (var i = 0; i < 6; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }
        console.Input.PushKey(ConsoleKey.Enter);
        // Server select -> Exit.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask, bookmarkStore: null, connector);

        exit.ShouldBe(0);
        session.ToolName.ShouldBe("Ping");
        session.Arguments.ShouldNotBeNull();
        session.Arguments!.Count.ShouldBe(0);
        console.Output.ShouldContain("RESULT-OK");
    }

    [Fact]
    public async Task ToolDetail_WithoutConnector_OffersNoCallAction()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [SingleToolServer()]);

        console.Input.PushKey(ConsoleKey.Enter);                 // server alpha
        console.Input.PushKey(ConsoleKey.DownArrow);             // Tools
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);             // -> Ping
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);             // tool actions -> Back (Bookmark, Back)
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);             // tools list -> [Back]
        console.Input.PushKey(ConsoleKey.Enter);
        for (var i = 0; i < 6; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);         // section -> Back
        }
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);             // server -> Exit
        console.Input.PushKey(ConsoleKey.Enter);

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask);

        exit.ShouldBe(0);
        console.Output.ShouldNotContain("Call tool");
    }
}
