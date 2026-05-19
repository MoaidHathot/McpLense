using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace McpLense.Scanning.Checks;

/// <summary>Helpers shared across MCP-session-driven checks.</summary>
internal static class CheckSessionHelpers
{
    public static JsonNode? ToNode(object value)
        => JsonSerializer.SerializeToNode(value, AuthCheck.SerializerOptions);

    public static async Task<McpClient?> TryGetSessionAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Server.Kind != ConnectionKind.Http)
        {
            return null;
        }

        return await context.GetSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a property by name (case-insensitive) via reflection. Returns null when the
    /// property is missing or its value is null - matches the SDK-tolerant pattern used by
    /// McpExecutor / McpSessionInspector.
    /// </summary>
    public static object? GetProp(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        var prop = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetValue(instance);
    }

    public static bool? GetBoolProp(object? instance, string name)
    {
        var value = GetProp(instance, name);
        return value switch
        {
            bool b => b,
            null => null,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static JsonNode? SafeNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return value switch
            {
                JsonNode node => node.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                _ => JsonSerializer.SerializeToNode(value, AuthCheck.SerializerOptions)
            };
        }
        catch
        {
            return null;
        }
    }
}
