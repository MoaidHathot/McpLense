using System.Text.Json.Nodes;

namespace McpLense.Learning;

/// <summary>
/// Generates a structurally-complete example value from a JSON Schema (a tool's <c>inputSchema</c>),
/// so a newcomer sees the exact argument shape to fill in. Pure and defensive: an unknown/missing
/// schema yields an empty object, and it honors <c>default</c> and <c>enum</c> when present. The
/// values are type-appropriate placeholders (<c>""</c> / <c>0</c> / <c>false</c> / <c>[]</c>), not
/// real data - the output is a template to edit, surfaced by <c>call --example</c> and the TUI.
/// </summary>
public static class SchemaSampleGenerator
{
    private const int MaxDepth = 8;

    /// <summary>Builds an example value for the given schema node.</summary>
    public static JsonNode Generate(JsonNode? schema) => Generate(schema, 0);

    /// <summary>The required property names declared at the top level of an object schema (sorted).</summary>
    public static IReadOnlyList<string> RequiredProperties(JsonNode? schema)
    {
        if (schema?["required"] is not JsonArray required)
        {
            return [];
        }

        return required
            .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    private static JsonNode Generate(JsonNode? schema, int depth)
    {
        if (schema is not JsonObject obj || depth > MaxDepth)
        {
            return new JsonObject();
        }

        if (obj["default"] is { } def)
        {
            return def.DeepClone();
        }

        if (obj["enum"] is JsonArray { Count: > 0 } e && e[0] is { } first)
        {
            return first.DeepClone();
        }

        // Composed schemas: take the first branch as a representative shape.
        foreach (var key in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (obj[key] is JsonArray { Count: > 0 } branches && branches[0] is { } branch)
            {
                return Generate(branch, depth + 1);
            }
        }

        return TypeOf(obj) switch
        {
            "object" => GenerateObject(obj, depth),
            "array" => GenerateArray(obj, depth),
            "string" => JsonValue.Create(string.Empty),
            "integer" or "number" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(false),
            "null" => JsonValue.Create((string?)null)!,
            // No explicit type: an object when it declares properties, otherwise a neutral empty
            // object ("any"). A tool's top-level inputSchema is conceptually an object, so a bare {}
            // schema maps to {} rather than a bare string.
            _ => obj["properties"] is JsonObject ? GenerateObject(obj, depth) : new JsonObject()
        };
    }

    private static JsonObject GenerateObject(JsonObject schema, int depth)
    {
        var result = new JsonObject();
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (name, propSchema) in properties)
            {
                result[name] = Generate(propSchema, depth + 1);
            }
        }

        return result;
    }

    private static JsonArray GenerateArray(JsonObject schema, int depth)
    {
        var array = new JsonArray();
        if (schema["items"] is { } items)
        {
            array.Add(Generate(items, depth + 1));
        }

        return array;
    }

    private static string? TypeOf(JsonObject schema)
    {
        var type = schema["type"];
        if (type is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s;
        }

        // Union types ("type": ["string","null"]): pick the first non-null entry.
        if (type is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue tv && tv.TryGetValue<string>(out var t) && t != "null")
                {
                    return t;
                }
            }
        }

        return null;
    }
}
