using System.Text.Json.Nodes;
using McpLense;
using Shouldly;
using Spectre.Console.Testing;
using Xunit;

namespace McpLense.UnitTests.Tui;

/// <summary>
/// Drives the interactive-invocation path of the TUI end to end with a scripted console and a
/// recording <see cref="IMcpInvoker"/> fake, so no transport is opened. Proves that selecting a
/// tool and choosing "Call tool" elicits arguments and dispatches to the invoker.
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

    private sealed class RecordingInvoker : IMcpInvoker
    {
        public string? ServerName;
        public string? ToolName;
        public JsonObject? Arguments;

        public Task<InvokeResult> CallToolAsync(string serverName, string toolName, JsonObject arguments, CancellationToken cancellationToken)
        {
            ServerName = serverName;
            ToolName = toolName;
            Arguments = arguments;
            return Task.FromResult(new InvokeResult("RESULT-OK", HasErrors: false));
        }

        public Task<InvokeResult> ReadResourceAsync(string serverName, string resourceOrTemplate, JsonObject? arguments, CancellationToken cancellationToken)
            => Task.FromResult(new InvokeResult("READ-OK", HasErrors: false));

        public Task<InvokeResult> GetPromptAsync(string serverName, string promptName, JsonObject arguments, CancellationToken cancellationToken)
            => Task.FromResult(new InvokeResult("PROMPT-OK", HasErrors: false));
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
    public async Task CallTool_NoArgs_InvokesInvokerAndShowsResult()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [SingleToolServer()]);
        var invoker = new RecordingInvoker();

        // Server select: pick "alpha" (index 0).
        console.Input.PushKey(ConsoleKey.Enter);
        // Section menu (Overview, Tools, ...): down to "Tools" (index 1).
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Tools list ([Search], [Back], Ping): down twice to "Ping".
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Tool actions (Call tool, Bookmark, Back): pick "Call tool" (index 0).
        console.Input.PushKey(ConsoleKey.Enter);
        // Confirm "Run now?": accept default (yes).
        console.Input.PushKey(ConsoleKey.Enter);
        // Tool actions again: down twice to "Back".
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Tools list again: down to "[Back]" (index 1).
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Section menu again: down 6 to "Back".
        for (var i = 0; i < 6; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }
        console.Input.PushKey(ConsoleKey.Enter);
        // Server select again: down to "Exit".
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var exit = await TuiApp.RenderAsync(report, console, () => Task.CompletedTask, bookmarkStore: null, invoker);

        exit.ShouldBe(0);
        invoker.ToolName.ShouldBe("Ping");
        invoker.ServerName.ShouldBe("alpha");
        invoker.Arguments.ShouldNotBeNull();
        invoker.Arguments!.Count.ShouldBe(0);
        console.Output.ShouldContain("RESULT-OK");
    }

    [Fact]
    public async Task ToolDetail_WithoutInvoker_OffersNoCallAction()
    {
        var console = NewConsole();
        var report = new InspectReport(DateTimeOffset.UtcNow, [SingleToolServer()]);

        // Navigate into the tool detail, then straight back out, then exit.
        console.Input.PushKey(ConsoleKey.Enter);                 // server alpha
        console.Input.PushKey(ConsoleKey.DownArrow);             // Tools
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);             // -> Ping
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        // Tool actions (Bookmark, Back) - no "Call tool" when invoker is null.
        console.Input.PushKey(ConsoleKey.DownArrow);             // -> Back (index 1)
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
