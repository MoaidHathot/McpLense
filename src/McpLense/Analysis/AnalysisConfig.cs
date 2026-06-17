using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpLense.Analysis;

/// <summary>
/// Config for the analysis (findings) layer, read from the top-level <c>analysis</c> block of
/// <c>McpLense.Config.json</c> (with a legacy nested location under <c>scan.analysis</c> also
/// honoured). Lets the user enable/disable individual rules, override their severity, and set the
/// default CI-gate threshold - all from config rather than a wall of CLI flags.
/// </summary>
public sealed class AnalysisConfig
{
    /// <summary>Per-rule overrides, keyed case-insensitively by rule id.</summary>
    [JsonPropertyName("rules")]
    public Dictionary<string, AnalysisRuleConfig> Rules { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default severity threshold for the CI gate (<c>analyze --fail-on</c> overrides it). One of
    /// info/low/medium/high/critical; null means "never fail on findings".
    /// </summary>
    [JsonPropertyName("failOn")]
    public string? FailOn { get; init; }

    /// <summary>Resolves the effective enabled state for a rule (config wins over the rule default).</summary>
    public bool IsRuleEnabled(string ruleId, bool defaultEnabled)
        => Rules.TryGetValue(ruleId, out var rule) && rule.Enabled.HasValue
            ? rule.Enabled.Value
            : defaultEnabled;

    /// <summary>Resolves the effective severity for a rule's findings (config override or the rule's own).</summary>
    public Severity SeverityFor(string ruleId, Severity natural)
        => Rules.TryGetValue(ruleId, out var rule) && Severities.TryParse(rule.Severity) is { } overridden
            ? overridden
            : natural;

    /// <summary>The configured CI-gate threshold, parsed; null when unset or unrecognised.</summary>
    public Severity? FailOnThreshold => Severities.TryParse(FailOn);
}

/// <summary>Per-rule config entry: enable/disable, severity override, and free-form future knobs.</summary>
public sealed class AnalysisRuleConfig
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    /// <summary>Any additional rule-specific knobs (reserved for rules that grow options).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
