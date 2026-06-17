using System.Text.Json.Serialization;

namespace McpLense.Analysis;

/// <summary>
/// One opinionated finding produced by a <see cref="IFindingRule"/> over the fact-only scan output.
/// A finding never invents data: <see cref="EvidencePath"/> points at the exact location in the scan
/// report it was derived from, and <see cref="Evidence"/> quotes the relevant value, so a consumer
/// can always trace a finding back to the raw fact that triggered it.
/// </summary>
public sealed record Finding(
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("severity")] Severity Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("evidencePath")] string EvidencePath,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("remediation")] string Remediation);

/// <summary>All findings for a single scanned server.</summary>
public sealed record ServerFindings(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings);

/// <summary>
/// Top-level findings report - the output of the analysis layer. Deliberately separate from
/// <see cref="McpLense.Scanning.ScanReport"/>: facts and judgements never share a document, so a
/// consumer that only wants facts is never handed opinions, and vice versa.
/// </summary>
public sealed record FindingsReport(
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("servers")] IReadOnlyList<ServerFindings> Servers)
{
    /// <summary>Highest severity present across all servers, or null when there are no findings.</summary>
    public Severity? MaxSeverity
        => Servers.SelectMany(s => s.Findings).Select(f => (Severity?)f.Severity).Max();

    /// <summary>True when any finding meets or exceeds <paramref name="threshold"/> (CI-gate check).</summary>
    public bool Exceeds(Severity threshold)
        => Servers.Any(s => s.Findings.Any(f => f.Severity >= threshold));

    /// <summary>Total finding count across all servers.</summary>
    public int Count => Servers.Sum(s => s.Findings.Count);
}
