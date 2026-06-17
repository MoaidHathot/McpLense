using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dumpify;
using McpLense.Scanning;

namespace McpLense;

internal static class OutputRenderer
{
    public static string Render(OutputFormat format, object payload, JsonSerializerOptions jsonOptions) => format switch
    {
        OutputFormat.Json => JsonSerializer.Serialize(payload, jsonOptions),
        OutputFormat.Jsonl => RenderJsonl(payload, jsonOptions),
        OutputFormat.Dumpify => payload.DumpText(),
        OutputFormat.Sarif => SarifRenderer.Render(payload, jsonOptions),
        _ => TextFormatter.Format(payload, jsonOptions)
    };

    /// <summary>
    /// Renders the payload as JSON Lines (NDJSON): one self-contained JSON document per line.
    /// For <see cref="ScanReport"/> the layout is:
    /// <code>
    /// {"kind":"header","generatedAt":"...","schemaVersion":"1","serverCount":N}
    /// {"kind":"server","name":"...","transport":"...","target":"...","checks":{...},"timings":{...}}
    /// ...
    /// {"kind":"trailer","servers":N}
    /// </code>
    /// Fleet consumers can stream-read this without buffering the full report. For other
    /// payload shapes we fall back to a single line (still valid JSONL).
    /// </summary>
    private static string RenderJsonl(object payload, JsonSerializerOptions jsonOptions)
    {
        // Compact-per-line: clone the supplied options but drop indentation. Indented JSON is
        // unreadable as JSONL because pretty-printed objects span multiple lines.
        var lineOptions = new JsonSerializerOptions(jsonOptions) { WriteIndented = false };

        if (payload is ScanReport report)
        {
            var sb = new StringBuilder();
            var header = new JsonObject
            {
                ["kind"] = "header",
                ["generatedAt"] = report.GeneratedAt.ToString("o"),
                ["schemaVersion"] = report.SchemaVersion,
                ["serverCount"] = report.Servers.Count
            };
            sb.Append(header.ToJsonString(lineOptions));
            sb.Append('\n');

            foreach (var server in report.Servers)
            {
                // Round-trip the server through System.Text.Json so camelCase / converters
                // apply, then graft on the "kind" discriminator.
                var node = JsonSerializer.SerializeToNode(server, lineOptions) as JsonObject
                           ?? new JsonObject();
                node["kind"] = "server";
                sb.Append(node.ToJsonString(lineOptions));
                sb.Append('\n');
            }

            var trailer = new JsonObject
            {
                ["kind"] = "trailer",
                ["servers"] = report.Servers.Count
            };
            sb.Append(trailer.ToJsonString(lineOptions));
            return sb.ToString();
        }

        // Non-scan payloads: emit a single compact line so JSONL consumers still get one
        // document per line.
        return JsonSerializer.Serialize(payload, lineOptions);
    }
}
