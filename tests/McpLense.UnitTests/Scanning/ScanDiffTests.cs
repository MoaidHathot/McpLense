using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using McpLense.Scanning;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning;

public class ScanDiffTests
{
    private static ScanReport ReportWith(params (string Target, IReadOnlyDictionary<string, JsonNode?> Checks)[] servers)
    {
        return new ScanReport(
            GeneratedAt: DateTimeOffset.UnixEpoch,
            SchemaVersion: "1",
            Servers: servers.Select(s => new ServerScanResult(
                Name: new Uri(s.Target).Host,
                Transport: "http",
                Target: s.Target,
                Checks: s.Checks,
                Timings: new Dictionary<string, double>())).ToArray());
    }

    [Fact]
    public void Diff_EqualReports_ProducesNoServerEntries()
    {
        var before = ReportWith(("https://x/mcp", new Dictionary<string, JsonNode?>
        {
            ["hashing"] = JsonNode.Parse("""{"serverFingerprint":"abc"}""")
        }));
        var after = ReportWith(("https://x/mcp", new Dictionary<string, JsonNode?>
        {
            ["hashing"] = JsonNode.Parse("""{"serverFingerprint":"abc"}""")
        }));

        ScanDiff.Diff(before, after).Servers.ShouldBeEmpty();
    }

    [Fact]
    public void Diff_AddedServer_AppearsAsAdded()
    {
        var before = ReportWith();
        var after = ReportWith(("https://new/mcp", new Dictionary<string, JsonNode?>()));

        var diff = ScanDiff.Diff(before, after);
        diff.Servers.ShouldHaveSingleItem().Status.ShouldBe("added");
    }

    [Fact]
    public void Diff_RemovedServer_AppearsAsRemoved()
    {
        var before = ReportWith(("https://gone/mcp", new Dictionary<string, JsonNode?>()));
        var after = ReportWith();

        var diff = ScanDiff.Diff(before, after);
        diff.Servers.ShouldHaveSingleItem().Status.ShouldBe("removed");
    }

    [Fact]
    public void Diff_ChangedTool_AppearsInToolsChangedArray()
    {
        var beforeTools = JsonNode.Parse("""{"items":[{"name":"echo","description":"Echo a string","contentHash":"h1"}]}""");
        var afterTools = JsonNode.Parse("""{"items":[{"name":"echo","description":"Echo a string (now also runs shell)","contentHash":"h2"}]}""");

        var before = ReportWith(("https://x/mcp", new Dictionary<string, JsonNode?>
        {
            ["tools"] = beforeTools,
            ["hashing"] = JsonNode.Parse("""{"serverFingerprint":"abc"}""")
        }));
        var after = ReportWith(("https://x/mcp", new Dictionary<string, JsonNode?>
        {
            ["tools"] = afterTools,
            ["hashing"] = JsonNode.Parse("""{"serverFingerprint":"def"}""")
        }));

        var diff = ScanDiff.Diff(before, after);
        var server = diff.Servers.ShouldHaveSingleItem();
        server.Status.ShouldBe("changed");
        server.ServerFingerprintBefore.ShouldBe("abc");
        server.ServerFingerprintAfter.ShouldBe("def");

        var toolsDiff = server.Checks["tools"].ShouldNotBeNull().AsObject();
        toolsDiff["changed"].ShouldNotBeNull();
        toolsDiff["changed"]!.AsArray().ShouldHaveSingleItem()!["id"]!.GetValue<string>().ShouldBe("echo");
    }
}
