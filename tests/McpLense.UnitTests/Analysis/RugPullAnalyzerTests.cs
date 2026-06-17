using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using McpLense.Analysis;
using McpLense.Scanning;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Analysis;

public class RugPullAnalyzerTests
{
    private static ScanReport ReportWithHashing(string hashingJson, string target = "https://x/mcp")
    {
        var checks = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            ["hashing"] = JsonNode.Parse(hashingJson)
        };
        var server = new ServerScanResult("s", "http", target, checks, new Dictionary<string, double>());
        return new ScanReport(DateTimeOffset.UnixEpoch, "1", [server]);
    }

    [Fact]
    public void Snapshot_CapturesHashes()
    {
        var report = ReportWithHashing("""{"serverFingerprint":"fp","toolHashes":{"Echo":"h1"},"promptHashes":{},"resourceHashes":{}}""");

        var baseline = RugPullAnalyzer.Snapshot(report);

        baseline.Servers.ShouldHaveSingleItem();
        baseline.Servers[0].Target.ShouldBe("https://x/mcp");
        baseline.Servers[0].ServerFingerprint.ShouldBe("fp");
        baseline.Servers[0].ToolHashes["Echo"].ShouldBe("h1");
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var baseline = RugPullAnalyzer.Snapshot(ReportWithHashing("""{"toolHashes":{"Echo":"h1"}}"""));
        var restored = RugPullAnalyzer.Deserialize(RugPullAnalyzer.Serialize(baseline));

        restored.ShouldNotBeNull();
        restored!.Servers[0].ToolHashes["Echo"].ShouldBe("h1");
    }

    [Fact]
    public void Compare_ChangedHash_IsHigh()
    {
        var approved = RugPullAnalyzer.Snapshot(ReportWithHashing("""{"toolHashes":{"Echo":"OLD"}}"""));
        var current = ReportWithHashing("""{"toolHashes":{"Echo":"NEW"}}""");

        var byTarget = RugPullAnalyzer.Compare(current, approved);

        var finding = byTarget["https://x/mcp"].ShouldHaveSingleItem();
        finding.RuleId.ShouldBe("rug-pull");
        finding.Severity.ShouldBe(Severity.High);
        finding.Title.ShouldContain("Echo");
        finding.Title.ShouldContain("changed");
    }

    [Fact]
    public void Compare_AddedItem_IsMedium()
    {
        var approved = RugPullAnalyzer.Snapshot(ReportWithHashing("""{"toolHashes":{"Echo":"h1"}}"""));
        var current = ReportWithHashing("""{"toolHashes":{"Echo":"h1","New":"h2"}}""");

        var finding = RugPullAnalyzer.Compare(current, approved)["https://x/mcp"].ShouldHaveSingleItem();
        finding.Severity.ShouldBe(Severity.Medium);
        finding.Title.ShouldContain("New");
    }

    [Fact]
    public void Compare_RemovedItem_IsInfo()
    {
        var approved = RugPullAnalyzer.Snapshot(ReportWithHashing("""{"toolHashes":{"Echo":"h1","Gone":"h2"}}"""));
        var current = ReportWithHashing("""{"toolHashes":{"Echo":"h1"}}""");

        var finding = RugPullAnalyzer.Compare(current, approved)["https://x/mcp"].ShouldHaveSingleItem();
        finding.Severity.ShouldBe(Severity.Info);
        finding.Title.ShouldContain("Gone");
    }

    [Fact]
    public void Compare_Unchanged_NoFindings()
    {
        var approved = RugPullAnalyzer.Snapshot(ReportWithHashing("""{"toolHashes":{"Echo":"h1"}}"""));
        var current = ReportWithHashing("""{"toolHashes":{"Echo":"h1"}}""");

        RugPullAnalyzer.Compare(current, approved).ShouldBeEmpty();
    }

    [Fact]
    public void Compare_UnknownTarget_IsIgnored()
    {
        var approved = RugPullAnalyzer.Snapshot(ReportWithHashing("""{"toolHashes":{"Echo":"h1"}}""", "https://a/mcp"));
        var current = ReportWithHashing("""{"toolHashes":{"Echo":"NEW"}}""", "https://b/mcp");

        RugPullAnalyzer.Compare(current, approved).ShouldBeEmpty();
    }
}
