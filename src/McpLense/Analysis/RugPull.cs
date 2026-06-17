using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpLense.Scanning;

namespace McpLense.Analysis;

/// <summary>
/// An approved snapshot of a server's surface: the per-item content hashes from the <c>hashing</c>
/// check at the moment the user approved the server. Comparing a later scan against this detects a
/// "rug pull" - a tool/prompt/resource whose behavior changed after it was trusted.
/// </summary>
public sealed record ApprovalBaseline(
    [property: JsonPropertyName("approvedAt")] DateTimeOffset ApprovedAt,
    [property: JsonPropertyName("servers")] IReadOnlyList<ApprovedServer> Servers);

public sealed record ApprovedServer(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("serverFingerprint")] string? ServerFingerprint,
    [property: JsonPropertyName("toolHashes")] IReadOnlyDictionary<string, string> ToolHashes,
    [property: JsonPropertyName("promptHashes")] IReadOnlyDictionary<string, string> PromptHashes,
    [property: JsonPropertyName("resourceHashes")] IReadOnlyDictionary<string, string> ResourceHashes);

/// <summary>
/// Snapshots and compares <see cref="ApprovalBaseline"/>s using the fact-only <c>hashing</c> check.
/// Pure - no I/O beyond JSON (de)serialization helpers - so it is unit-testable from hand-built
/// scan reports.
/// </summary>
public static class RugPullAnalyzer
{
    /// <summary>Rule id used for rug-pull findings (config can override its severity / disable it).</summary>
    public const string RuleId = "rug-pull";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Captures the current per-item hashes as an approval snapshot.</summary>
    public static ApprovalBaseline Snapshot(ScanReport report)
    {
        var servers = report.Servers.Select(s => new ApprovedServer(
            s.Target,
            s.Check("hashing").Str("serverFingerprint"),
            HashMap(s, "toolHashes"),
            HashMap(s, "promptHashes"),
            HashMap(s, "resourceHashes"))).ToList();
        return new ApprovalBaseline(DateTimeOffset.UtcNow, servers);
    }

    public static string Serialize(ApprovalBaseline baseline) => JsonSerializer.Serialize(baseline, FileOptions);

    public static ApprovalBaseline? Deserialize(string json) => JsonSerializer.Deserialize<ApprovalBaseline>(json, FileOptions);

    /// <summary>
    /// Compares a current scan against an approved baseline, yielding rug-pull findings per target:
    /// changed item hashes (High - the dangerous case), newly added items (Medium), and removed
    /// items (Info). Only servers present in both the scan and the baseline are compared.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Finding>> Compare(ScanReport current, ApprovalBaseline approved)
    {
        var byTarget = approved.Servers.ToDictionary(s => s.Target, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<Finding>>(StringComparer.Ordinal);

        foreach (var server in current.Servers)
        {
            if (!byTarget.TryGetValue(server.Target, out var baseline))
            {
                continue;
            }

            var findings = new List<Finding>();
            CompareCategory(findings, "tool", "toolHashes", HashMap(server, "toolHashes"), baseline.ToolHashes);
            CompareCategory(findings, "prompt", "promptHashes", HashMap(server, "promptHashes"), baseline.PromptHashes);
            CompareCategory(findings, "resource", "resourceHashes", HashMap(server, "resourceHashes"), baseline.ResourceHashes);
            if (findings.Count > 0)
            {
                result[server.Target] = findings;
            }
        }

        return result;
    }

    private static void CompareCategory(
        List<Finding> findings,
        string label,
        string mapName,
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> approved)
    {
        foreach (var (key, approvedHash) in approved)
        {
            if (!current.TryGetValue(key, out var currentHash))
            {
                findings.Add(new Finding(RuleId, Severity.Info,
                    $"{Capitalize(label)} '{key}' was removed since the approved baseline",
                    $"checks.hashing.{mapName}[{key}]", $"approved {Short(approvedHash)} -> (absent)",
                    "Confirm the removal is expected; a tool disappearing can break clients that relied on it."));
            }
            else if (!string.Equals(currentHash, approvedHash, StringComparison.Ordinal))
            {
                findings.Add(new Finding(RuleId, Severity.High,
                    $"{Capitalize(label)} '{key}' changed since the approved baseline (possible rug-pull)",
                    $"checks.hashing.{mapName}[{key}]", $"approved {Short(approvedHash)} -> current {Short(currentHash)}",
                    "Re-review this item: its definition changed after it was approved. Re-approve only if the change is intended."));
            }
        }

        foreach (var key in current.Keys)
        {
            if (!approved.ContainsKey(key))
            {
                findings.Add(new Finding(RuleId, Severity.Medium,
                    $"{Capitalize(label)} '{key}' is new since the approved baseline",
                    $"checks.hashing.{mapName}[{key}]", $"current {Short(current[key])}",
                    "Review the newly added item before trusting it, then re-approve the baseline."));
            }
        }
    }

    private static IReadOnlyDictionary<string, string> HashMap(ServerScanResult server, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (server.Check("hashing")?[property] is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (value.AsStr() is { } hash)
                {
                    map[key] = hash;
                }
            }
        }

        return map;
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
