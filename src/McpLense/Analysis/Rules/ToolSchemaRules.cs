using McpLense.Scanning;

namespace McpLense.Analysis.Rules;

/// <summary>
/// A tool whose input JSON Schema does not lock down <c>additionalProperties</c> accepts arbitrary
/// fields - a wide attack surface for an LLM that gets to fill the arguments.
/// </summary>
public sealed class OpenShapeInputRule : IFindingRule
{
    public string Id => "open-shape-input";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        foreach (var tool in facts.ToolItems())
        {
            var name = tool.Str("name") ?? "(unnamed)";
            if (tool["schemaFingerprint"].Bool("hasAdditionalProperties") == true)
            {
                yield return new Finding(
                    Id,
                    Severity.Medium,
                    $"Tool '{name}' accepts open-shape input (additionalProperties not restricted)",
                    $"checks.tools.items[name={name}].schemaFingerprint.hasAdditionalProperties",
                    "true",
                    "Set additionalProperties:false in the tool input schema, or constrain it, so the host can't pass arbitrary fields.");
            }
        }
    }
}

/// <summary>
/// A tool without a declared <c>destructiveHint</c> is unclassified by the server: a host that
/// auto-invokes tools can't tell whether it is safe to call without confirmation.
/// </summary>
public sealed class MissingDestructiveHintRule : IFindingRule
{
    public string Id => "missing-destructive-hint";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        foreach (var tool in facts.ToolItems())
        {
            var name = tool.Str("name") ?? "(unnamed)";
            if (tool.ArrayContains("missingAnnotations", "destructiveHint"))
            {
                yield return new Finding(
                    Id,
                    Severity.Low,
                    $"Tool '{name}' does not declare a destructiveHint annotation",
                    $"checks.tools.items[name={name}].missingAnnotations",
                    "destructiveHint",
                    "Declare the destructiveHint annotation so hosts can decide whether to require user confirmation before invoking.");
            }
        }
    }
}
