using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpLense;
using McpLense.Scanning;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Output;

/// <summary>
/// Locks in the JSONL wire shape consumers will stream-read. Each line MUST be a
/// self-contained JSON document; the layout is header / one-per-server / trailer.
/// </summary>
public class OutputRendererJsonlTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Jsonl_ScanReport_EmitsHeaderServersAndTrailer()
    {
        var report = new ScanReport(
            GeneratedAt: DateTimeOffset.UnixEpoch,
            SchemaVersion: "1",
            Servers: new List<ServerScanResult>
            {
                new(Name: "a", Transport: "http", Target: "https://a/mcp", Checks: new Dictionary<string, JsonNode?>(), Timings: new Dictionary<string, double>()),
                new(Name: "b", Transport: "http", Target: "https://b/mcp", Checks: new Dictionary<string, JsonNode?>(), Timings: new Dictionary<string, double>())
            });

        var output = OutputRenderer.Render(OutputFormat.Jsonl, report, JsonOptions);

        // Each newline must terminate a self-contained JSON document. The last line has no
        // trailing newline (it's the trailer) but is otherwise a regular JSON object.
        var lines = output.Split('\n');
        lines.Length.ShouldBe(4); // header + 2 servers + trailer

        var header = JsonNode.Parse(lines[0])!.AsObject();
        header["kind"]!.GetValue<string>().ShouldBe("header");
        header["serverCount"]!.GetValue<int>().ShouldBe(2);
        header["schemaVersion"]!.GetValue<string>().ShouldBe("1");

        var server1 = JsonNode.Parse(lines[1])!.AsObject();
        server1["kind"]!.GetValue<string>().ShouldBe("server");
        server1["name"]!.GetValue<string>().ShouldBe("a");

        var server2 = JsonNode.Parse(lines[2])!.AsObject();
        server2["name"]!.GetValue<string>().ShouldBe("b");

        var trailer = JsonNode.Parse(lines[3])!.AsObject();
        trailer["kind"]!.GetValue<string>().ShouldBe("trailer");
        trailer["servers"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void Jsonl_EveryLine_IsCompactSingleLineJson()
    {
        // Indented JSON would break stream-readers: enforce that the renderer flattens.
        var report = new ScanReport(
            GeneratedAt: DateTimeOffset.UnixEpoch,
            SchemaVersion: "1",
            Servers: new[]
            {
                new ServerScanResult("a", "http", "https://a/mcp", new Dictionary<string, JsonNode?>(), new Dictionary<string, double>())
            });

        var output = OutputRenderer.Render(OutputFormat.Jsonl, report, JsonOptions);

        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // A compact JSON document never contains an internal newline.
            line.ShouldNotContain("\n");
            // ... and must round-trip cleanly through System.Text.Json.
            JsonNode.Parse(line).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Jsonl_NonScanPayload_StillSingleCompactLine()
    {
        var output = OutputRenderer.Render(OutputFormat.Jsonl, new { hello = "world" }, JsonOptions);

        output.ShouldNotContain("\n");
        var node = JsonNode.Parse(output)!.AsObject();
        node["hello"]!.GetValue<string>().ShouldBe("world");
    }
}
