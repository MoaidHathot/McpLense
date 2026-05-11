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

[Collection("HttpTestServer")]
public class HttpExecutorTests
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly HttpTestServerFixture _fixture;

    public HttpExecutorTests(HttpTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    private ParsedCommand BuildCommand(
        AppCommand command,
        string url,
        TransportPreference transport,
        string? subject = null,
        JsonObject? arguments = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool progressEnabled = false,
        TimeSpan? timeout = null)
    {
        var target = new TargetOptions(
            ConfigPaths: [],
            ServerNames: [],
            ProfilePaths: [],
            DisplayName: "http-test-server",
            Url: new Uri(url, UriKind.Absolute),
            Transport: transport,
            Headers: headers ?? new Dictionary<string, string>(),
            Command: null,
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

        return new ParsedCommand(
            Command: command,
            Subject: subject,
            Arguments: arguments,
            Format: OutputFormat.Json,
            Timeout: timeout ?? HttpTimeout,
            Target: target,
            ProgressEnabled: progressEnabled);
    }

    [Fact]
    public async Task Inspect_HttpAuto_ReturnsServerWithToolsAndPrompts()
    {
        var command = BuildCommand(AppCommand.Inspect, _fixture.BaseUrl, TransportPreference.Auto);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<InspectReport>();
        report.Servers.Count.ShouldBe(1);

        var server = report.Servers[0];
        server.Name.ShouldBe("http-test-server");
        server.Transport.ShouldBe("http");
        server.Error.ShouldBeNull();
        server.Capabilities.Tools.ShouldBeTrue();
        server.Capabilities.Prompts.ShouldBeTrue();
        server.Capabilities.Resources.ShouldBeTrue();

        server.Tools.Items.Select(tool => tool.Name).ShouldContain("Echo");
        server.Tools.Items.Select(tool => tool.Name).ShouldContain("Add");
        server.Tools.Items.Select(tool => tool.Name).ShouldContain("GetHeader");
        server.Prompts.Items.Select(prompt => prompt.Name).ShouldContain("Greet");
        server.Resources.Items.ShouldContain(resource => resource.Uri == "config://app/settings");
    }

    [Fact]
    public async Task Tools_HttpAuto_ListsAllRegisteredTools()
    {
        var command = BuildCommand(AppCommand.Tools, _fixture.BaseUrl, TransportPreference.Auto);

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
        names.ShouldContain("GetHeader");
    }

    [Fact]
    public async Task Resources_HttpAuto_ListsConfigResource()
    {
        var command = BuildCommand(AppCommand.Resources, _fixture.BaseUrl, TransportPreference.Auto);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ResourceListReport>();
        report.Servers[0].Items.ShouldContain(resource => resource.Uri == "config://app/settings");
    }

    [Fact]
    public async Task Prompts_HttpAuto_ListsRegisteredPrompts()
    {
        var command = BuildCommand(AppCommand.Prompts, _fixture.BaseUrl, TransportPreference.Auto);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<PromptListReport>();
        var names = report.Servers[0].Items.Select(prompt => prompt.Name).ToArray();
        names.ShouldContain("Greet");
        names.ShouldContain("CodeReview");
    }

    [Fact]
    public async Task Call_Echo_HttpAuto_ReturnsEchoedText()
    {
        var arguments = new JsonObject { ["message"] = "ping-http" };
        var command = BuildCommand(
            AppCommand.Call,
            _fixture.BaseUrl,
            TransportPreference.Auto,
            subject: "Echo",
            arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        report.Result.ShouldNotBeNull();
        report.Result!.IsError.ShouldNotBe(true);
        var textBlock = report.Result.Content.FirstOrDefault(block => block.Kind == "text");
        textBlock.ShouldNotBeNull();
        textBlock!.Text.ShouldNotBeNull().ShouldContain("echo: ping-http");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("streamable-http")]
    [InlineData("sse")]
    public async Task Call_Add_AcrossTransports_ReturnsSum(string transportLabel)
    {
        var (url, transport) = ResolveTransport(transportLabel);
        var arguments = new JsonObject { ["a"] = 17, ["b"] = 25 };
        var command = BuildCommand(
            AppCommand.Call,
            url,
            transport,
            subject: "Add",
            arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        var textBlock = report.Result!.Content.FirstOrDefault(block => block.Kind == "text");
        textBlock.ShouldNotBeNull();
        textBlock!.Text.ShouldNotBeNull().ShouldContain("42");
    }

    [Fact]
    public async Task Call_GetHeader_PropagatesCustomHeader_ReturnsHeaderValue()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Mcplense-Test"] = "round-tripped"
        };
        var arguments = new JsonObject { ["name"] = "X-Mcplense-Test" };
        var command = BuildCommand(
            AppCommand.Call,
            _fixture.BaseUrl,
            TransportPreference.Auto,
            subject: "GetHeader",
            arguments: arguments,
            headers: headers);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        var textBlock = report.Result!.Content.FirstOrDefault(block => block.Kind == "text");
        textBlock.ShouldNotBeNull();
        textBlock!.Text.ShouldNotBeNull().ShouldContain("round-tripped");
    }

    [Fact]
    public async Task Call_GetHeader_WithoutCustomHeader_ReturnsMissingMarker()
    {
        var arguments = new JsonObject { ["name"] = "X-Mcplense-Test" };
        var command = BuildCommand(
            AppCommand.Call,
            _fixture.BaseUrl,
            TransportPreference.Auto,
            subject: "GetHeader",
            arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        var textBlock = report.Result!.Content.FirstOrDefault(block => block.Kind == "text");
        textBlock.ShouldNotBeNull();
        textBlock!.Text.ShouldNotBeNull().ShouldContain("<missing>");
    }

    [Fact]
    public async Task Inspect_HttpAuto_BadPort_ReportsError()
    {
        var command = BuildCommand(
            AppCommand.Inspect,
            "http://127.0.0.1:1/",
            TransportPreference.Auto,
            timeout: TimeSpan.FromSeconds(5));

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeTrue();
        var report = outcome.Payload.ShouldBeOfType<InspectReport>();
        report.Servers[0].Error.ShouldNotBeNull();
    }

    private (string Url, TransportPreference Transport) ResolveTransport(string label) => label switch
    {
        "auto" => (_fixture.BaseUrl, TransportPreference.Auto),
        "streamable-http" => (_fixture.BaseUrl, TransportPreference.StreamableHttp),
        "sse" => (_fixture.SseUrl, TransportPreference.Sse),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Unknown transport label.")
    };
}
