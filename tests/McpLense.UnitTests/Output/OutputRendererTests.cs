using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Output;

public class OutputRendererTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Render_Json_ProducesIndentedJson()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, []);

        var output = OutputRenderer.Render(OutputFormat.Json, report, JsonOptions);

        output.ShouldContain("\"generatedAt\":");
        output.ShouldContain("\"servers\": []");
    }

    [Fact]
    public void Render_Text_DispatchesToTextFormatter()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, [
            new ServerInspection(
                Name: "demo",
                Transport: "stdio",
                Target: "node demo.js",
                Capabilities: new CapabilitySnapshot(true, false, false, false, false),
                Tools: new SectionResult<ToolInfo>(true, [new ToolInfo("echo", "say hi", null)]),
                Resources: new SectionResult<ResourceInfo>(false, []),
                ResourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
                Prompts: new SectionResult<PromptInfo>(false, []))
        ]);

        var output = OutputRenderer.Render(OutputFormat.Text, report, JsonOptions);

        output.ShouldContain("demo [stdio] node demo.js");
        output.ShouldContain("tools: 1");
        output.ShouldContain("- echo: say hi");
    }

    [Fact]
    public void Render_Dumpify_ProducesNonEmptyString()
    {
        var report = new InspectReport(DateTimeOffset.UnixEpoch, []);

        var output = OutputRenderer.Render(OutputFormat.Dumpify, report, JsonOptions);

        output.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Render_TextFallback_ForUnknownPayload_UsesJson()
    {
        var payload = new { name = "x", value = 42 };

        var output = OutputRenderer.Render(OutputFormat.Text, payload, JsonOptions);

        output.ShouldContain("\"name\": \"x\"");
        output.ShouldContain("\"value\": 42");
    }
}
