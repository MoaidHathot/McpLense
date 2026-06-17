using System.Text.Json.Nodes;
using McpLense.Scanning;

namespace McpLense.Analysis;

/// <summary>
/// Safe, null-tolerant navigation over the camelCase JSON a check emits into
/// <see cref="ServerScanResult.Checks"/>. Rules use these so a missing check, a missing field, or a
/// mis-typed value yields a benign null/empty instead of an exception (a check may not have run).
/// </summary>
internal static class Facts
{
    public static JsonNode? Check(this ServerScanResult facts, string id)
        => facts.Checks.TryGetValue(id, out var node) ? node : null;

    public static JsonArray? Array(this JsonNode? node, string property)
        => node?[property] as JsonArray;

    public static string? Str(this JsonNode? node, string property)
        => node?[property] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string? AsStr(this JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(this JsonNode? node, string property)
        => node?[property] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static int? Int(this JsonNode? node, string property)
        => node?[property] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static double? Number(this JsonNode? node, string property)
        => node?[property] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>True when a string array property contains <paramref name="value"/> (ordinal-insensitive).</summary>
    public static bool ArrayContains(this JsonNode? node, string property, string value)
    {
        if (node?[property] is not JsonArray array)
        {
            return false;
        }

        foreach (var item in array)
        {
            if (item.AsStr() is { } s && string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The <c>tools</c> check's item objects (empty when the check didn't run).</summary>
    public static IEnumerable<JsonNode> ToolItems(this ServerScanResult facts)
        => Items(facts.Check("tools").Array("items"));

    /// <summary>The <c>prompts</c> check's item objects.</summary>
    public static IEnumerable<JsonNode> PromptItems(this ServerScanResult facts)
        => Items(facts.Check("prompts").Array("items"));

    private static IEnumerable<JsonNode> Items(JsonArray? array)
    {
        if (array is null)
        {
            yield break;
        }

        foreach (var item in array)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }
}
