using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpLense.Analysis;

namespace McpLense;

/// <summary>
/// Renders a <see cref="FindingsReport"/> as SARIF 2.1.0 so findings flow into GitHub code scanning
/// and other SARIF-aware security tooling. Severity maps to the SARIF level (critical/high -&gt;
/// error, medium -&gt; warning, low/info -&gt; note); each finding's evidence path becomes a logical
/// location and the target URL an artifact location, so a result is traceable back to the server and
/// the exact fact it was derived from. Non-findings payloads fall back to plain JSON.
/// </summary>
internal static class SarifRenderer
{
    private const string Version = "2.1.0";
    private const string InformationUri = "https://github.com/MoaidHathot/McpLense";
    private const string HelpBase = "https://github.com/MoaidHathot/McpLense/blob/main/docs/analysis-rules.md";

    public static string Render(object payload, JsonSerializerOptions jsonOptions)
    {
        var findings = payload switch
        {
            FindingsReport report => report,
            AnalyzedScanReport analyzed => analyzed.Findings,
            _ => null
        };

        if (findings is null)
        {
            // SARIF only describes findings; anything else just serializes as JSON.
            return JsonSerializer.Serialize(payload, jsonOptions);
        }

        var results = new JsonArray();
        var ruleIds = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var server in findings.Servers)
        {
            foreach (var finding in server.Findings)
            {
                ruleIds.Add(finding.RuleId);
                results.Add(Result(finding, server.Target));
            }
        }

        var rules = new JsonArray();
        foreach (var id in ruleIds)
        {
            rules.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = id,
                ["helpUri"] = $"{HelpBase}#built-in-rules"
            });
        }

        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = Version,
            ["runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject
                        {
                            ["name"] = "McpLense",
                            ["informationUri"] = InformationUri,
                            ["version"] = ToolVersion(),
                            ["rules"] = rules
                        }
                    },
                    ["results"] = results
                }
            }
        };

        return sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Result(Finding finding, string target) => new()
    {
        ["ruleId"] = finding.RuleId,
        ["level"] = Level(finding.Severity),
        ["message"] = new JsonObject { ["text"] = finding.Title + (string.IsNullOrEmpty(finding.Remediation) ? string.Empty : $" — {finding.Remediation}") },
        ["locations"] = new JsonArray
        {
            new JsonObject
            {
                ["physicalLocation"] = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject { ["uri"] = target }
                },
                ["logicalLocations"] = new JsonArray
                {
                    new JsonObject { ["fullyQualifiedName"] = finding.EvidencePath, ["kind"] = "member" }
                }
            }
        },
        ["properties"] = new JsonObject
        {
            ["severity"] = finding.Severity.ToWire(),
            ["target"] = target,
            ["evidence"] = finding.Evidence
        }
    };

    private static string Level(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => "error",
        Severity.Medium => "warning",
        _ => "note"
    };

    private static string ToolVersion()
        => typeof(SarifRenderer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(SarifRenderer).Assembly.GetName().Version?.ToString()
           ?? "0.0.0";
}
