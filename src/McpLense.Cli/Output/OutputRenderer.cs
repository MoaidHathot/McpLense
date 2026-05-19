using System.Text.Json;
using Dumpify;

namespace McpLense;

internal static class OutputRenderer
{
    public static string Render(OutputFormat format, object payload, JsonSerializerOptions jsonOptions) => format switch
    {
        OutputFormat.Json => JsonSerializer.Serialize(payload, jsonOptions),
        OutputFormat.Dumpify => payload.DumpText(),
        _ => TextFormatter.Format(payload, jsonOptions)
    };
}
