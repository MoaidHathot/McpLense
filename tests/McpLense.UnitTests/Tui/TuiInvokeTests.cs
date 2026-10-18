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

        public ServerReference Server { get; init; } = new("alpha", "stdio", "dotnet exec foo.dll");

        public Task SetLoggingLevelAsync(ModelContextProtocol.Protocol.LoggingLevel level, CancellationToken cancellationToken)
            => Task.CompletedTask;

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

    [Theory]
    [InlineData("", null)]
    [InlineData("50", 50)]
    public async Task CallTool_ReleaseCommunicationsSchema_InvokesAndReturnsToMenu(string skipInput, int? expectedSkip)
    {
        var console = NewConsole();
        // Input schema from get_recent_azure_updates, with descriptions shortened.
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "filter": { "description": "OData filter", "type": "string", "default": null },
            "search": { "description": "Title search", "type": "string", "default": null },
            "skip": { "description": "Pagination offset", "type": ["integer", "null"], "default": null },
            "include_facets": { "description": "Include facet counts", "type": "boolean", "default": false }
          }
        }
        """);
        var tool = new ToolInfo("get_recent_azure_updates", "Recent Azure updates", schema);
        var server = SingleToolServer() with
        {
            Name = "www.microsoft.com",
            Transport = "http",
            Target = "https://www.microsoft.com/releasecommunications/mcp",
            Tools = new SectionResult<ToolInfo>(true, [tool])
        };
        var report = new InspectReport(DateTimeOffset.UtcNow, [server]);
        var session = new RecordingSession { Server = new(server.Name, server.Transport, server.Target) };
        McpSessionConnector connector = (_, _) => Task.FromResult<IMcpSession>(session);
        var pauses = 0;

        console.Input.PushCharacter('2');                       // sections -> Tools
        console.Input.PushKey(ConsoleKey.Enter);                // open the tool detail
        console.Input.PushKey(ConsoleKey.Enter);                // Call tool
        console.Input.PushTextWithEnter(string.Empty);          // filter -> skip
        console.Input.PushTextWithEnter("storage");             // search
        console.Input.PushTextWithEnter(skipInput);             // optional nullable integer
        console.Input.PushKey(ConsoleKey.Enter);                // include_facets -> default false
        console.Input.PushKey(ConsoleKey.Enter);                // Run now? -> yes
        console.Input.PushKey(ConsoleKey.Escape);               // tool actions -> tools list
        console.Input.PushKey(ConsoleKey.Escape);               // tools list -> sections
        console.Input.PushCharacter('1');                       // Overview remains accessible
        console.Input.PushCharacter('q');                       // exit explicitly

        var exit = await TuiApp.RenderAsync(report, console, () =>
        {
            pauses++;
            return Task.CompletedTask;
        }, bookmarkStore: null, connector);

        exit.ShouldBe(0);
        session.ToolName.ShouldBe(tool.Name);
        session.Arguments.ShouldNotBeNull();
        session.Arguments["search"]!.GetValue<string>().ShouldBe("storage");
        session.Arguments["include_facets"]!.GetValue<bool>().ShouldBeFalse();
        session.Arguments.ContainsKey("filter").ShouldBeFalse();
        session.Arguments.ContainsKey("skip").ShouldBe(expectedSkip.HasValue);
        if (expectedSkip.HasValue)
        {
            session.Arguments["skip"]!.GetValue<long>().ShouldBe(expectedSkip.Value);
        }
        console.Output.ShouldContain("skip (integer | null)");
        console.Output.ShouldContain("RESULT-OK");
        pauses.ShouldBe(2, "the result and the subsequently opened overview should both be reached");
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

    [Fact]
    public async Task CallTool_WhenServerInitiatesTraffic_ShowsItAfterResult()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [SingleToolServer()]);
        var interaction = new TuiServerInteraction();

        // A session that, while "running" the tool, captures a server-initiated elicitation +
        // notification through the shared interaction - exactly what the SDK handlers would do.
        McpSessionConnector connector = (_, _) =>
        {
            var session = new InteractingSession(interaction);
            return Task.FromResult<IMcpSession>(session);
        };

        console.Input.PushKey(ConsoleKey.Enter);                 // server alpha
        console.Input.PushKey(ConsoleKey.DownArrow);             // Tools
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);             // -> Ping
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);                 // Call tool
        console.Input.PushKey(ConsoleKey.Enter);                 // confirm Run now?
        console.Input.PushKey(ConsoleKey.DownArrow);             // tool actions -> Back
        console.Input.PushKey(ConsoleKey.DownArrow);
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

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask, bookmarkStore: null, connector, interaction);

        exit.ShouldBe(0);
        console.Output.ShouldContain("server-initiated");
        console.Output.ShouldContain("elicitation/create");
    }

    /// <summary>Session fake whose tool call pushes server-initiated traffic into the interaction.</summary>
    private sealed class InteractingSession(TuiServerInteraction interaction) : IMcpSession
    {
        public ServerReference Server { get; } = new("alpha", "stdio", "dotnet exec foo.dll");

        public Task SetLoggingLevelAsync(ModelContextProtocol.Protocol.LoggingLevel level, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ToolInfo>>([]);

        public Task<IReadOnlyList<PromptInfo>> ListPromptsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PromptInfo>>([]);

        public async Task<ToolCallReport> CallToolAsync(string toolName, JsonObject arguments, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
        {
            await interaction.ElicitAsync(new ModelContextProtocol.Protocol.ElicitRequestParams { Message = "confirm?" }, cancellationToken);
            await interaction.OnNotificationAsync("notifications/message", null, cancellationToken);
            return new ToolCallReport(
                DateTimeOffset.UnixEpoch, Server, toolName, arguments, [],
                new CallResultView(IsError: false, StructuredContent: null, Meta: null,
                    Content: [new ContentBlockView("text", Text: "RESULT-OK")]));
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
}
