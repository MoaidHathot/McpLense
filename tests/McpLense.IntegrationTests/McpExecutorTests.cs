using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests;

[Collection("McpExecutor")]
public class McpExecutorTests
{
    private static readonly TimeSpan StdioTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static ParsedCommand BuildCommand(
        AppCommand command,
        string? subject = null,
        JsonObject? arguments = null,
        bool progressEnabled = false)
    {
        var target = new TargetOptions(
            ConfigPaths: [],
            ServerNames: [],
            ProfilePaths: [],
            DisplayName: "test-server",
            Url: null,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Command: "dotnet",
            CommandArguments: ["exec", TestServerLocator.TestServerDll],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

        return new ParsedCommand(
            Command: command,
            Subject: subject,
            Arguments: arguments,
            Format: OutputFormat.Json,
            Timeout: StdioTimeout,
            Target: target,
            ProgressEnabled: progressEnabled);
    }

    [Fact]
    public async Task Inspect_ReturnsServerWithToolsAndPrompts()
    {
        var command = BuildCommand(AppCommand.Inspect);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<InspectReport>();
        report.Servers.Count.ShouldBe(1);

        var server = report.Servers[0];
        server.Name.ShouldBe("test-server");
        server.Transport.ShouldBe("stdio");
        server.Error.ShouldBeNull();
        server.Capabilities.Tools.ShouldBeTrue();
        server.Capabilities.Prompts.ShouldBeTrue();
        server.Capabilities.Resources.ShouldBeTrue();

        server.Tools.Items.Select(tool => tool.Name).ShouldContain("Echo");
        server.Tools.Items.Select(tool => tool.Name).ShouldContain("Add");
        server.Prompts.Items.Select(prompt => prompt.Name).ShouldContain("Greet");
        server.Resources.Items.ShouldContain(resource => resource.Uri == "config://app/settings");
    }

    [Fact]
    public async Task Tools_ListsAllRegisteredTools()
    {
        var command = BuildCommand(AppCommand.Tools);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolListReport>();
        report.Servers.Count.ShouldBe(1);

        var names = report.Servers[0].Items.Select(tool => tool.Name).ToArray();
        names.ShouldContain("Echo");
        names.ShouldContain("Add");
        names.ShouldContain("Divide");
        names.ShouldContain("RunWithProgress");
        names.ShouldContain("Boom");
    }

    [Fact]
    public async Task Resources_ListsConfigResource()
    {
        var command = BuildCommand(AppCommand.Resources);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ResourceListReport>();
        report.Servers[0].Items.ShouldContain(resource => resource.Uri == "config://app/settings");
    }

    [Fact]
    public async Task Prompts_ListsRegisteredPrompts()
    {
        var command = BuildCommand(AppCommand.Prompts);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<PromptListReport>();
        var names = report.Servers[0].Items.Select(prompt => prompt.Name).ToArray();
        names.ShouldContain("Greet");
        names.ShouldContain("CodeReview");
    }

    [Fact]
    public async Task Call_Echo_ReturnsEchoedText()
    {
        var arguments = new JsonObject { ["message"] = "ping" };
        var command = BuildCommand(AppCommand.Call, subject: "Echo", arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        report.Result.ShouldNotBeNull();
        report.Result!.IsError.ShouldNotBe(true);
        var textBlock = report.Result.Content.FirstOrDefault(block => block.Kind == "text");
        textBlock.ShouldNotBeNull();
        textBlock!.Text.ShouldNotBeNull().ShouldContain("echo: ping");
    }

    [Fact]
    public async Task Call_Add_ReturnsSum()
    {
        var arguments = new JsonObject { ["a"] = 7, ["b"] = 5 };
        var command = BuildCommand(AppCommand.Call, subject: "Add", arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        var textBlock = report.Result!.Content.FirstOrDefault(block => block.Kind == "text");
        textBlock.ShouldNotBeNull();
        textBlock!.Text.ShouldNotBeNull().ShouldContain("12");
    }

    [Fact]
    public async Task Call_Boom_FlagsAsError()
    {
        var command = BuildCommand(AppCommand.Call, subject: "Boom", arguments: new JsonObject());

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeTrue();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        // Either the call returned isError=true or the executor reported a transport-level error.
        (report.Result?.IsError == true || report.Error is not null).ShouldBeTrue();
    }

    [Fact]
    public async Task Call_RunWithProgress_CollectsProgressEvents()
    {
        var arguments = new JsonObject { ["steps"] = 3 };
        var command = BuildCommand(AppCommand.Call, subject: "RunWithProgress", arguments: arguments, progressEnabled: true);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        report.Progress.Count.ShouldBeGreaterThan(0);
        report.Progress.Last().Total.ShouldBe(3);
    }

    [Fact]
    public async Task Read_StaticResource_ReturnsTextContents()
    {
        var command = BuildCommand(AppCommand.Read, subject: "config://app/settings");

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ReadReport>();
        report.Result!.Contents.Count.ShouldBeGreaterThan(0);
        var content = report.Result.Contents[0];
        content.MimeType.ShouldBe("application/json");
        content.Text.ShouldNotBeNull().ShouldContain("\"theme\"");
    }

    [Fact]
    public async Task Read_ResourceTemplate_WithArguments_ReturnsArticle()
    {
        var command = BuildCommand(
            AppCommand.Read,
            subject: "docs://articles/hello",
            arguments: new JsonObject());

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ReadReport>();
        var content = report.Result!.Contents.FirstOrDefault();
        content.ShouldNotBeNull();
        content!.Text.ShouldNotBeNull().ShouldContain("Article hello");
    }

    [Fact]
    public async Task Prompt_Greet_ReturnsMessage()
    {
        var arguments = new JsonObject { ["name"] = "world" };
        var command = BuildCommand(AppCommand.Prompt, subject: "Greet", arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<PromptCallReport>();
        report.Result.ShouldNotBeNull();
        report.Result!.Messages.Count.ShouldBeGreaterThan(0);
        var message = report.Result.Messages[0];
        message.Content.ShouldNotBeNull();
        message.Content!.Text.ShouldNotBeNull().ShouldContain("Hello, world!");
    }

    [Fact]
    public async Task Prompt_CodeReview_ReturnsRenderedSnippet()
    {
        var arguments = new JsonObject
        {
            ["language"] = "csharp",
            ["code"] = "var x = 1;"
        };
        var command = BuildCommand(AppCommand.Prompt, subject: "CodeReview", arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<PromptCallReport>();
        report.Result!.Messages.Count.ShouldBeGreaterThan(0);
        var firstMessage = report.Result.Messages[0];
        firstMessage.Content.ShouldNotBeNull();
        firstMessage.Content!.Text.ShouldNotBeNull().ShouldContain("var x = 1;");
    }

    [Fact]
    public async Task Inspect_FailingCommand_ReportsError()
    {
        var target = new TargetOptions(
            ConfigPaths: [],
            ServerNames: [],
            ProfilePaths: [],
            DisplayName: "missing",
            Url: null,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Command: "definitely-not-a-real-command-xyz123",
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

        var command = new ParsedCommand(
            AppCommand.Inspect, null, null, OutputFormat.Json,
            TimeSpan.FromSeconds(5), target, false);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeTrue();
        var report = outcome.Payload.ShouldBeOfType<InspectReport>();
        report.Servers[0].Error.ShouldNotBeNull();
    }
}
