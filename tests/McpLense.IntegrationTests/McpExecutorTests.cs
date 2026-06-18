using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.Analysis;
using McpLense.Scanning;
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
        report.Servers[0].Templates.ShouldContain(template => template.UriTemplate == "docs://articles/{id}");
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

    // --- Own-flow command dispatch (characterization before the ICommandHandler split) -------
    // The 7 list/invoke commands above are well covered; these pin the commands that own their
    // own resolve/auth flow, which the dictionary-dispatch refactor moves into handlers.

    [Fact]
    public async Task AuthScan_StdioTarget_ClassifiesAsStdio()
    {
        var outcome = await McpExecutor.ExecuteAsync(BuildCommand(AppCommand.AuthScan), JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<AuthScanReport>();
        report.Servers.Count.ShouldBe(1);
        report.Servers[0].Classification.ShouldBe(AuthClassifications.Stdio);
    }

    [Fact]
    public async Task FetchResource_StaticResource_ReturnsContents()
    {
        var command = BuildCommand(AppCommand.FetchResource, subject: "config://app/settings");

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ReadReport>();
        report.Result!.Contents.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FetchResource_NoSubject_Throws()
    {
        var command = BuildCommand(AppCommand.FetchResource, subject: null);

        await Should.ThrowAsync<UserInputException>(
            () => McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None));
    }

    [Fact]
    public async Task Scan_StdioTarget_ProducesReport()
    {
        var outcome = await McpExecutor.ExecuteAsync(BuildCommand(AppCommand.Scan), JsonOptions, CancellationToken.None);

        var report = outcome.Payload.ShouldBeOfType<ScanReport>();
        report.Servers.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Analyze_StdioTarget_ProducesFindingsReport()
    {
        var outcome = await McpExecutor.ExecuteAsync(BuildCommand(AppCommand.Analyze), JsonOptions, CancellationToken.None);

        // No --fail-on -> never gates, so HasErrors stays false regardless of findings.
        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<FindingsReport>();
        report.Servers.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Explain_StdioTarget_ProducesNarrative()
    {
        var outcome = await McpExecutor.ExecuteAsync(BuildCommand(AppCommand.Explain), JsonOptions, CancellationToken.None);

        var report = outcome.Payload.ShouldBeOfType<McpLense.Learning.ExplainReport>();
        report.Servers.ShouldHaveSingleItem().Lines.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Call_Example_ProducesArgumentTemplateWithoutInvoking()
    {
        var command = BuildCommand(AppCommand.Call, subject: "Add") with { Example = true };

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        var report = outcome.Payload.ShouldBeOfType<ToolExampleReport>();
        report.ToolName.ShouldBe("Add");
        report.Error.ShouldBeNull();
        report.Example.ShouldNotBeNull();
    }

    [Fact]
    public async Task Observe_StdioTarget_ProducesReport()
    {
        // Observe holds the session open for command.Timeout; keep the window short.
        var target = new TargetOptions(
            ConfigPaths: [], ServerNames: [], ProfilePaths: [], DisplayName: "test-server", Url: null,
            Transport: TransportPreference.Auto, Headers: new Dictionary<string, string>(),
            Command: "dotnet", CommandArguments: ["exec", TestServerLocator.TestServerDll],
            WorkingDirectory: null, Environment: new Dictionary<string, string>(), AuthOverrides: AuthOverrides.Empty);
        var command = new ParsedCommand(
            AppCommand.Observe, null, null, OutputFormat.Json, TimeSpan.FromSeconds(3), target, false);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.Payload.ShouldBeOfType<ScanReport>();
    }

    [Fact]
    public async Task Login_AllWithNoProfiles_Throws()
    {
        // Tier-None command: short-circuits before any target resolution. With auto-discovery
        // disabled (TestModuleInitializer) and no --profiles, --all has nothing to act on.
        var target = new TargetOptions(
            ConfigPaths: [], ServerNames: [], ProfilePaths: [], DisplayName: null, Url: null,
            Transport: TransportPreference.Auto, Headers: new Dictionary<string, string>(),
            Command: null, CommandArguments: [], WorkingDirectory: null,
            Environment: new Dictionary<string, string>(), AuthOverrides: new AuthOverrides(All: true));
        var command = new ParsedCommand(
            AppCommand.Login, null, null, OutputFormat.Json, TimeSpan.FromSeconds(5), target, false);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None));
        ex.Message.ShouldContain("profile");
    }
}
