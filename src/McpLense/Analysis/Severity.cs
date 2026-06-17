namespace McpLense.Analysis;

/// <summary>
/// Severity of a <see cref="Finding"/>, ordered low-to-high so thresholds (CI gates) and sorting
/// work by numeric comparison. These are the only opinionated labels McpLense emits, and they live
/// exclusively in the findings layer - the underlying scan checks stay fact-only.
/// </summary>
public enum Severity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>Parsing helpers for <see cref="Severity"/> (config files / CLI flags use the names).</summary>
public static class Severities
{
    /// <summary>Case-insensitive parse; returns null for null/empty/unrecognised input.</summary>
    public static Severity? TryParse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "info" => Severity.Info,
            "low" => Severity.Low,
            "medium" => Severity.Medium,
            "high" => Severity.High,
            "critical" => Severity.Critical,
            _ => null
        };

    /// <summary>Lowercase wire name (matches the config / CLI vocabulary).</summary>
    public static string ToWire(this Severity severity) => severity switch
    {
        Severity.Info => "info",
        Severity.Low => "low",
        Severity.Medium => "medium",
        Severity.High => "high",
        Severity.Critical => "critical",
        _ => severity.ToString().ToLowerInvariant()
    };
}
