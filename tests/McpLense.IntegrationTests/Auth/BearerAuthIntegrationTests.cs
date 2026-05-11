using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests.Auth;

[Collection("BearerHttpTestServer")]
public class BearerAuthIntegrationTests
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly BearerHttpTestServerFixture _fixture;

    public BearerAuthIntegrationTests(BearerHttpTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static ParsedCommand BuildCommand(
        AppCommand command,
        string url,
        AuthOverrides authOverrides,
        string? subject = null,
        JsonObject? arguments = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var target = new TargetOptions(
            ConfigPaths: [],
            ServerNames: [],
            ProfilePaths: [],
            DisplayName: "bearer-test-server",
            Url: new Uri(url, UriKind.Absolute),
            Transport: TransportPreference.Auto,
            Headers: headers ?? new Dictionary<string, string>(),
            Command: null,
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: authOverrides);

        return new ParsedCommand(
            Command: command,
            Subject: subject,
            Arguments: arguments,
            Format: OutputFormat.Json,
            Timeout: HttpTimeout,
            Target: target,
            ProgressEnabled: false);
    }

    [Fact]
    public async Task Inspect_NoAuth_FailsWith401()
    {
        var command = BuildCommand(AppCommand.Inspect, _fixture.BaseUrl, AuthOverrides.Empty);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeTrue();
        var report = outcome.Payload.ShouldBeOfType<InspectReport>();
        var error = report.Servers[0].Error.ShouldNotBeNull();
        // Error message comes from the underlying HttpClient pipeline -- 401/Unauthorized somewhere.
        error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Inspect_BearerCorrectToken_Succeeds()
    {
        var command = BuildCommand(
            AppCommand.Inspect,
            _fixture.BaseUrl,
            new AuthOverrides(Kind: AuthKind.Bearer, Token: BearerHttpTestServerFixture.TestToken));

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<InspectReport>();
        report.Servers.Count.ShouldBe(1);

        var server = report.Servers[0];
        server.Error.ShouldBeNull();
        server.Tools.Items.Select(tool => tool.Name).ShouldContain("Echo");
    }

    [Fact]
    public async Task Inspect_BearerWrongToken_Fails()
    {
        var command = BuildCommand(
            AppCommand.Inspect,
            _fixture.BaseUrl,
            new AuthOverrides(Kind: AuthKind.Bearer, Token: "wrong-token"));

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeTrue();
        var report = outcome.Payload.ShouldBeOfType<InspectReport>();
        report.Servers[0].Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Call_Echo_BearerCorrectToken_ReturnsEchoedText()
    {
        var arguments = new JsonObject { ["message"] = "auth-ping" };
        var command = BuildCommand(
            AppCommand.Call,
            _fixture.BaseUrl,
            new AuthOverrides(Kind: AuthKind.Bearer, Token: BearerHttpTestServerFixture.TestToken),
            subject: "Echo",
            arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        var textBlock = report.Result!.Content.FirstOrDefault(block => block.Kind == "text");
        textBlock.ShouldNotBeNull();
        textBlock!.Text.ShouldNotBeNull().ShouldContain("echo: auth-ping");
    }

    [Fact]
    public async Task Call_GetHeader_BearerHandlerInjectsAuthorization()
    {
        // The MCP server's GetHeader tool returns the inbound Authorization header value.
        // This proves the BearerHandler is wired through HttpClient and lands at the server.
        var arguments = new JsonObject { ["name"] = "Authorization" };
        var command = BuildCommand(
            AppCommand.Call,
            _fixture.BaseUrl,
            new AuthOverrides(Kind: AuthKind.Bearer, Token: BearerHttpTestServerFixture.TestToken),
            subject: "GetHeader",
            arguments: arguments);

        var outcome = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);

        outcome.HasErrors.ShouldBeFalse();
        var report = outcome.Payload.ShouldBeOfType<ToolCallReport>();
        var textBlock = report.Result!.Content.FirstOrDefault(block => block.Kind == "text");
        textBlock.ShouldNotBeNull();
        textBlock!.Text.ShouldNotBeNull().ShouldContain($"Bearer {BearerHttpTestServerFixture.TestToken}");
    }
}
