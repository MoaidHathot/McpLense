using System.Text.Json.Nodes;

namespace McpLense.Scanning;

/// <summary>
/// Top-level report emitted by <c>mcplense scan</c> and any embedder that runs the
/// pipeline. Each server entry's <see cref="ServerScanResult.Checks"/> dictionary holds the
/// verbatim output of every check that ran for that server (keyed by
/// <see cref="IScanCheck.Id"/>). The wire shape is stable; new fields are added only as new
/// check ids, never by reshaping existing ones.
/// </summary>
public sealed record ScanReport(
    DateTimeOffset GeneratedAt,
    string SchemaVersion,
    IReadOnlyList<ServerScanResult> Servers);

public sealed record ServerScanResult(
    string Name,
    string Transport,
    string Target,
    IReadOnlyDictionary<string, JsonNode?> Checks,
    IReadOnlyDictionary<string, double> Timings,
    string? Error = null);
