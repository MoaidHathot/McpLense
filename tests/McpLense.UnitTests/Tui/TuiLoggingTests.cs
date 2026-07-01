using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Spectre.Console.Testing;
using Xunit;

namespace McpLense.UnitTests.Tui;

public class TuiLoggingTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 200;
        return console;
    }

    /// <summary>An IMcpSession that records logging/setLevel calls and exposes the interaction sink.</summary>
    private sealed class LoggingSession : IMcpSession
    {
        public readonly List<LoggingLevel> LevelsSet = new();

        public ServerReference Server { get; } = new("logsrv", "stdio", "dotnet exec log.dll");

        public Task SetLoggingLevelAsync(LoggingLevel level, CancellationToken cancellationToken)
        {
            LevelsSet.Add(level);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ToolInfo>>([]);
        public Task<IReadOnlyList<PromptInfo>> ListPromptsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PromptInfo>>([]);
        public Task<ToolCallReport> CallToolAsync(string toolName, System.Text.Json.Nodes.JsonObject arguments, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
            => Task.FromResult(new ToolCallReport(DateTimeOffset.UnixEpoch, Server, toolName, arguments, [], null));
        public Task<ReadReport> ReadResourceAsync(string resourceOrTemplate, System.Text.Json.Nodes.JsonObject? arguments, CancellationToken cancellationToken)
            => Task.FromResult(new ReadReport(DateTimeOffset.UnixEpoch, Server, resourceOrTemplate, arguments, new ReadResourceView([])));
        public Task<PromptCallReport> GetPromptAsync(string promptName, System.Text.Json.Nodes.JsonObject arguments, CancellationToken cancellationToken)
            => Task.FromResult(new PromptCallReport(DateTimeOffset.UnixEpoch, Server, promptName, arguments, new PromptResultView(null, [])));
        public Task<IReadOnlyList<string>> CompletePromptArgumentAsync(string promptName, string argumentName, string partialValue, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> CompleteTemplateArgumentAsync(string uriTemplate, string argumentName, string partialValue, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ServerInspection Server(bool logging)
        => new(
            Name: "logsrv",
            Transport: "stdio",
            Target: "dotnet exec log.dll",
            Capabilities: new CapabilitySnapshot(Tools: true, Resources: false, Prompts: false, Logging: logging, Completions: false),
            Tools: new SectionResult<ToolInfo>(true, [new ToolInfo("Ping", "no-arg", null)]),
            Resources: new SectionResult<ResourceInfo>(false, []),
            ResourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
            Prompts: new SectionResult<PromptInfo>(false, []));

    [Fact]
    public async Task LoggingCapableServer_AutoSetsMostVerboseLevel_OnEntry()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [Server(logging: true)]);
        var interaction = new TuiServerInteraction();
        var mcp = new LoggingSession();
        McpSessionConnector connector = (_, _) => Task.FromResult<IMcpSession>(mcp);

        // Single server auto-selects into the section menu; then exit.
        console.Input.PushCharacter('q');

        await TuiApp.RenderAsync(report, console, () => Task.CompletedTask, bookmarkStore: null, connector, interaction);

        mcp.LevelsSet.ShouldContain(LoggingLevel.Debug);
    }

    [Fact]
    public async Task NonLoggingServer_DoesNotSetLevel_AndHasNoLogsSection()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [Server(logging: false)]);
        var interaction = new TuiServerInteraction();
        var mcp = new LoggingSession();
        McpSessionConnector connector = (_, _) => Task.FromResult<IMcpSession>(mcp);

        console.Input.PushCharacter('q');

        await TuiApp.RenderAsync(report, console, () => Task.CompletedTask, bookmarkStore: null, connector, interaction);

        mcp.LevelsSet.ShouldBeEmpty();
        console.Output.ShouldNotContain("server logs");
    }

    [Fact]
    public async Task LoggingCapableServer_ShowsLogsSectionAndPersistentTail()
    {
        var console = NewConsole();
        var interaction = new TuiServerInteraction();
        // A log arrives before/while the menu is shown.
        await interaction.OnNotificationAsync("notifications/message",
            System.Text.Json.Nodes.JsonNode.Parse("""{"level":"warning","logger":"svc","data":"disk almost full"}"""),
            CancellationToken.None);

        var report = new InspectReport(DateTimeOffset.UtcNow, [Server(logging: true)]);
        var mcp = new LoggingSession();
        McpSessionConnector connector = (_, _) => Task.FromResult<IMcpSession>(mcp);

        console.Input.PushCharacter('q'); // exit from the section menu

        await TuiApp.RenderAsync(report, console, () => Task.CompletedTask, bookmarkStore: null, connector, interaction);

        var output = console.Output;
        output.ShouldContain("Logs");            // the section entry (with a count badge)
        output.ShouldContain("server logs");     // the persistent tail rule
        output.ShouldContain("disk almost full"); // the tail line itself
    }
}
