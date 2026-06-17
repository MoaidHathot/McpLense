using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpLense;
using McpLense.Analysis;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Output;

public class SarifRendererTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static FindingsReport Report(params Finding[] findings)
        => new(DateTimeOffset.UnixEpoch, [new ServerFindings("s", "https://x/mcp", findings)]);

    private static Finding F(string ruleId, Severity severity)
        => new(ruleId, severity, $"{ruleId} title", $"checks.{ruleId}.path", "evidence", "fix it");

    private static JsonNode Render(object payload)
        => JsonNode.Parse(OutputRenderer.Render(OutputFormat.Sarif, payload, JsonOptions))!;

    [Fact]
    public void Render_ProducesSarif210Envelope()
    {
        var sarif = Render(Report(F("mixed-content", Severity.High)));

        sarif["version"]!.GetValue<string>().ShouldBe("2.1.0");
        sarif["runs"]!.AsArray().Count.ShouldBe(1);
        sarif["runs"]![0]!["tool"]!["driver"]!["name"]!.GetValue<string>().ShouldBe("McpLense");
    }

    [Theory]
    [InlineData(Severity.Critical, "error")]
    [InlineData(Severity.High, "error")]
    [InlineData(Severity.Medium, "warning")]
    [InlineData(Severity.Low, "note")]
    [InlineData(Severity.Info, "note")]
    public void Render_MapsSeverityToSarifLevel(Severity severity, string expectedLevel)
    {
        var sarif = Render(Report(F("rule", severity)));
        sarif["runs"]![0]!["results"]![0]!["level"]!.GetValue<string>().ShouldBe(expectedLevel);
    }

    [Fact]
    public void Render_CarriesRuleIdTargetAndEvidencePath()
    {
        var sarif = Render(Report(F("open-shape-input", Severity.Medium)));
        var result = sarif["runs"]![0]!["results"]![0]!;

        result["ruleId"]!.GetValue<string>().ShouldBe("open-shape-input");
        result["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"]!.GetValue<string>().ShouldBe("https://x/mcp");
        result["locations"]![0]!["logicalLocations"]![0]!["fullyQualifiedName"]!.GetValue<string>().ShouldBe("checks.open-shape-input.path");
    }

    [Fact]
    public void Render_EmitsRuleMetadataForEachDistinctRule()
    {
        var sarif = Render(Report(F("a", Severity.High), F("a", Severity.High), F("b", Severity.Low)));
        sarif["runs"]![0]!["tool"]!["driver"]!["rules"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Render_AnalyzedScanReport_UsesFindings()
    {
        var scan = new McpLense.Scanning.ScanReport(DateTimeOffset.UnixEpoch, "1", []);
        var sarif = Render(new AnalyzedScanReport(scan, Report(F("weak-cors", Severity.High))));
        sarif["runs"]![0]!["results"]!.AsArray().Count.ShouldBe(1);
    }
}
