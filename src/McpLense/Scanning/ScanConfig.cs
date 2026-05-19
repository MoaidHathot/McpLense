using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace McpLense.Scanning;

/// <summary>
/// Parsed shape of the <c>scan</c> top-level block in <c>McpLense.Config.json</c>. Each
/// check has its own sub-object under <see cref="Checks"/>, keyed by check id. The pipeline
/// passes the per-check sub-object to <see cref="ScanContext.Config"/>'s lookup and the
/// check reads its own knobs from there.
/// </summary>
/// <remarks>
/// Unknown check ids in the config emit a warn-on-stderr message (per user decision) but do
/// not fail the scan; future versions / extensions may add ids that older readers don't know.
/// </remarks>
public sealed class ScanConfig
{
    /// <summary>Per-check sub-objects. Lookup is case-insensitive on the check id.</summary>
    [JsonPropertyName("checks")]
    public Dictionary<string, JsonObject> Checks { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Output-related knobs (baseline directory etc.).</summary>
    [JsonPropertyName("output")]
    public ScanOutputConfig Output { get; init; } = new();

    /// <summary>Schema version reserved for future migrations.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Returns the parsed config block for a given check id, or null when nothing was
    /// configured (the check then uses its own defaults).
    /// </summary>
    public JsonObject? GetCheckConfig(string checkId)
        => Checks.TryGetValue(checkId, out var node) ? node : null;

    /// <summary>
    /// Resolves the effective enabled flag for a check, applying (in order):
    /// 1. <c>scan.checks.&lt;id&gt;.enabled</c> from the config file.
    /// 2. CLI <c>--enable</c> / <c>--disable</c> overrides (already parsed into the
    ///    <paramref name="enables"/> / <paramref name="disables"/> sets).
    /// 3. The check's own <see cref="IScanCheck.IsEnabledByDefault"/>.
    /// CLI flags win over file; file wins over default.
    /// </summary>
    public bool IsCheckEnabled(IScanCheck check, IReadOnlySet<string>? enables = null, IReadOnlySet<string>? disables = null)
    {
        if (disables is not null && disables.Contains(check.Id))
        {
            return false;
        }

        if (enables is not null && enables.Contains(check.Id))
        {
            return true;
        }

        if (Checks.TryGetValue(check.Id, out var node)
            && node.TryGetPropertyValue("enabled", out var enabledNode)
            && enabledNode is JsonValue v
            && v.TryGetValue<bool>(out var enabled))
        {
            return enabled;
        }

        return check.IsEnabledByDefault;
    }

    /// <summary>
    /// Returns the set of check ids mentioned in the config file. Used by the pipeline to
    /// warn (stderr) about ids that don't correspond to any registered check, so typos in
    /// the config don't silently degrade to "default behaviour".
    /// </summary>
    public IReadOnlySet<string> ConfiguredCheckIds
        => Checks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class ScanOutputConfig
{
    /// <summary>
    /// Base directory for baselines written via <c>--baseline</c>. Empty / unset means
    /// "current working directory". The CLI flag overrides the config value when supplied.
    /// </summary>
    [JsonPropertyName("baselineDir")]
    public string? BaselineDir { get; init; }

    /// <summary>Output format for baselines (json today; reserved for future formats).</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }
}
