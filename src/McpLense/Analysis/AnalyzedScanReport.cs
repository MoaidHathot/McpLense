using System.Text.Json.Serialization;
using McpLense.Scanning;

namespace McpLense.Analysis;

/// <summary>
/// The combined payload of <c>scan --findings</c>: the fact-only <see cref="ScanReport"/> and the
/// opinionated <see cref="FindingsReport"/> as two clearly-separated top-level keys. Facts and
/// judgements never interleave - a consumer can read just <c>scan</c> and ignore <c>findings</c>, or
/// vice versa.
/// </summary>
public sealed record AnalyzedScanReport(
    [property: JsonPropertyName("scan")] ScanReport Scan,
    [property: JsonPropertyName("findings")] FindingsReport Findings);
