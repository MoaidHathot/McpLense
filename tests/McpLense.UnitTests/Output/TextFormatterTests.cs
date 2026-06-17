using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Output;

public class TextFormatterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Format_InspectReport_RendersServerHeaderAndCapabilities()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, [
            new ServerInspection(
                Name: "demo",
                Transport: "stdio",
                Target: "node demo.js",
                Capabilities: new CapabilitySnapshot(true, true, false, false, false),
                Tools: new SectionResult<ToolInfo>(true, [
                    new ToolInfo("echo", "echo back", JsonNode.Parse("{\"type\":\"object\"}"))
                ]),
                Resources: new SectionResult<ResourceInfo>(true, [
                    new ResourceInfo("settings", "config://app/settings", "application/json", "App settings")
                ]),
                ResourceTemplates: new SectionResult<ResourceTemplateInfo>(true, []),
                Prompts: new SectionResult<PromptInfo>(false, []))
        ]);

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("demo [stdio] node demo.js");
        output.ShouldContain("capabilities: tools, resources");
        output.ShouldContain("tools: 1");
        output.ShouldContain("- echo: echo back");
        output.ShouldContain("schema:");
        output.ShouldContain("resources: 1");
        output.ShouldContain("- settings: config://app/settings [application/json]");
        output.ShouldContain("    App settings");
        output.ShouldContain("prompts: not supported");
        output.ShouldContain("resource templates: 0");
    }

    [Fact]
    public void Format_InspectReport_HandlesServerError()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, [
            new ServerInspection(
                Name: "broken",
                Transport: "http",
                Target: "https://example.com/mcp",
                Capabilities: new CapabilitySnapshot(false, false, false, false, false),
                Tools: new SectionResult<ToolInfo>(false, []),
                Resources: new SectionResult<ResourceInfo>(false, []),
                ResourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
                Prompts: new SectionResult<PromptInfo>(false, []),
                Error: "boom")
        ]);

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("broken [http] https://example.com/mcp");
        output.ShouldContain("error: boom");
    }

    [Fact]
    public void Format_InspectReport_AnonymousConnection_RendersAuthLine()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, [
            new ServerInspection(
                Name: "demo",
                Transport: "http",
                Target: "https://example.com/mcp",
                Capabilities: new CapabilitySnapshot(true, false, false, false, false),
                Tools: new SectionResult<ToolInfo>(true, []),
                Resources: new SectionResult<ResourceInfo>(false, []),
                ResourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
                Prompts: new SectionResult<PromptInfo>(false, []),
                AuthStatus: ConnectionAuthInfo.Anonymous)
        ]);

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("auth: anonymous (no credentials sent)");
    }

    [Fact]
    public void Format_InspectReport_AuthenticatedViaProfile_RendersProfileAndKind()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, [
            new ServerInspection(
                Name: "demo",
                Transport: "http",
                Target: "https://example.com/mcp",
                Capabilities: new CapabilitySnapshot(true, false, false, false, false),
                Tools: new SectionResult<ToolInfo>(true, []),
                Resources: new SectionResult<ResourceInfo>(false, []),
                ResourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
                Prompts: new SectionResult<PromptInfo>(false, []),
                AuthStatus: ConnectionAuthInfo.Authenticated("agent365-cli", AuthKind.AzureCli, "auto-pick"))
        ]);

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("auth: authenticated (profile=agent365-cli, kind=AzureCli)");
    }

    [Fact]
    public void DescribeConnectionAuth_StdioOrNull_ReturnsNull()
    {
        TextFormatter.DescribeConnectionAuth(null).ShouldBeNull();
        TextFormatter.DescribeConnectionAuth(ConnectionAuthInfo.None).ShouldBeNull();
    }

    [Fact]
    public void DescribeConnectionAuth_InlineBearer_OmitsProfile()
    {
        var inline = ConnectionAuthInfo.Authenticated(profile: null, AuthKind.Bearer, "inline");
        TextFormatter.DescribeConnectionAuth(inline).ShouldBe("authenticated (kind=Bearer)");
    }

    [Fact]
    public void Format_ToolListReport_RendersTools()
    {
        var report = new ToolListReport(DateTimeOffset.UnixEpoch, [
            new ServerItems<ToolInfo>("demo", "stdio", "node demo.js", [
                new ToolInfo("echo", "echo back", null),
                new ToolInfo("noop", null, null)
            ])
        ]);

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("tools: 2");
        output.ShouldContain("- echo: echo back");
        output.ShouldContain("- noop");
    }

    [Fact]
    public void Format_ResourceListReport_RendersResources()
    {
        var report = new ResourceListReport(DateTimeOffset.UnixEpoch, [
            new ServerResources("demo", "stdio", "node demo.js",
                [new ResourceInfo("settings", "config://app/settings", "application/json", null)],
                [new ResourceTemplateInfo("Article", "docs://articles/{id}", "text/markdown", null)])
        ]);

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("resources: 1");
        output.ShouldContain("- settings: config://app/settings [application/json]");
        output.ShouldContain("resource templates: 1");
        output.ShouldContain("- Article: docs://articles/{id} [text/markdown]");
    }

    [Fact]
    public void Format_PromptListReport_RendersArguments()
    {
        var report = new PromptListReport(DateTimeOffset.UnixEpoch, [
            new ServerItems<PromptInfo>("demo", "stdio", "node demo.js", [
                new PromptInfo("greet", "say hi", [
                    new PromptArgumentInfo("name", "user name", true)
                ])
            ])
        ]);

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("- greet: say hi");
        output.ShouldContain("arg: name (required): user name");
    }

    [Fact]
    public void Format_ToolCallReport_RendersProgressAndContent()
    {
        var report = new ToolCallReport(
            GeneratedAt: DateTimeOffset.UnixEpoch,
            Server: new ServerReference("demo", "stdio", "node demo.js"),
            ToolName: "ProcessData",
            Arguments: new JsonObject { ["count"] = 3 },
            Progress: [
                new ProgressUpdate(1, 3, "step 1", DateTimeOffset.UnixEpoch),
                new ProgressUpdate(3, 3, "done", DateTimeOffset.UnixEpoch)
            ],
            Result: new CallResultView(
                IsError: false,
                StructuredContent: JsonNode.Parse("{\"ok\":true}"),
                Meta: null,
                Content: [
                    new ContentBlockView("text", Text: "completed")
                ]));

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("tool: ProcessData");
        output.ShouldContain("arguments:");
        output.ShouldContain("\"count\": 3");
        output.ShouldContain("progress events: 2");
        output.ShouldContain("step 1");
        output.ShouldContain("is error: false");
        output.ShouldContain("content: 1");
        output.ShouldContain("- kind: text");
        output.ShouldContain("structured content:");
        output.ShouldContain("\"ok\": true");
    }

    [Fact]
    public void Format_ToolCallReport_RendersError()
    {
        var report = new ToolCallReport(
            GeneratedAt: DateTimeOffset.UnixEpoch,
            Server: new ServerReference("demo", "stdio", "node demo.js"),
            ToolName: "broken",
            Arguments: new JsonObject(),
            Progress: [],
            Result: null,
            Error: "exploded");

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("error: exploded");
    }

    [Fact]
    public void Format_ReadReport_RendersTextContent()
    {
        var report = new ReadReport(
            GeneratedAt: DateTimeOffset.UnixEpoch,
            Server: new ServerReference("demo", "stdio", "node demo.js"),
            Resource: "config://app/settings",
            Arguments: null,
            Result: new ReadResourceView([
                new ResourceContentView(
                    Kind: "text",
                    Uri: "config://app/settings",
                    MimeType: "application/json",
                    Text: "{\"theme\":\"dark\"}")
            ]));

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("resource: config://app/settings");
        output.ShouldContain("contents: 1");
        output.ShouldContain("- kind: text");
        output.ShouldContain("uri: config://app/settings");
        output.ShouldContain("mime: application/json");
        output.ShouldContain("{\"theme\":\"dark\"}");
    }

    [Fact]
    public void Format_PromptCallReport_RendersMessages()
    {
        var report = new PromptCallReport(
            GeneratedAt: DateTimeOffset.UnixEpoch,
            Server: new ServerReference("demo", "stdio", "node demo.js"),
            PromptName: "greet",
            Arguments: new JsonObject { ["name"] = "world" },
            Result: new PromptResultView(
                Description: "say hi",
                Messages: [
                    new PromptMessageView("user", new ContentBlockView("text", Text: "Hello, world!"))
                ]));

        var output = TextFormatter.Format(report, JsonOptions);

        output.ShouldContain("prompt: greet");
        output.ShouldContain("description: say hi");
        output.ShouldContain("messages: 1");
        output.ShouldContain("- role: user");
        output.ShouldContain("Hello, world!");
    }

    [Fact]
    public void Format_UnknownPayload_FallsBackToJson()
    {
        var payload = new { name = "x", value = 42 };

        var output = TextFormatter.Format(payload, JsonOptions);

        output.ShouldContain("\"name\": \"x\"");
        output.ShouldContain("\"value\": 42");
    }
}
