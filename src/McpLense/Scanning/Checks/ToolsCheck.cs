using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Enumerates the server's tools (verbatim) and folds in Tier 1 schema-fingerprint counts.
/// Every field of every tool is captured exactly as the server returned it; the
/// <c>schemaFingerprint</c> sub-object adds parameter counts, type histograms, format lists,
/// and presence flags for downstream classifiers to read without re-parsing the schema.
/// </summary>
internal sealed class ToolsCheck : IScanCheck
{
    public string Id => "tools";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var client = await CheckSessionHelpers.TryGetSessionAsync(context, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: context.SessionError ?? "No MCP session available.");
        }

        if (client.ServerCapabilities?.Tools is null)
        {
            return new CheckOutcome(Ran: true, Data: ToNode(new ToolsData(true, context.ActiveFetchedVia, null, [])), Error: null);
        }

        IList<McpClientTool> tools;
        try
        {
            tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckOutcome(Ran: true, Data: ToNode(new ToolsData(false, context.ActiveFetchedVia, $"{ex.GetType().Name}: {ex.Message}", [])), Error: null);
        }

        var items = tools.Select(MapTool).ToArray();
        return new CheckOutcome(Ran: true, Data: ToNode(new ToolsData(true, context.ActiveFetchedVia, null, items)), Error: null);
    }

    private static ToolEntryExtended MapTool(McpClientTool tool)
    {
        var protocolTool = tool.ProtocolTool;
        var annotations = protocolTool?.Annotations;
        var missing = ComputeMissingAnnotations(annotations);
        var inputSchema = CheckSessionHelpers.SafeNode(protocolTool?.InputSchema ?? tool.JsonSchema);
        var outputSchema = CheckSessionHelpers.SafeNode(protocolTool?.OutputSchema ?? tool.ReturnJsonSchema);
        var fingerprint = BuildSchemaFingerprint(inputSchema, outputSchema);

        return new ToolEntryExtended(
            Name: tool.Name,
            Title: protocolTool?.Title ?? tool.Title,
            Description: tool.Description,
            InputSchema: inputSchema,
            OutputSchema: outputSchema,
            Annotations: annotations is null ? null : new ToolAnnotationsView(
                Title: annotations.Title,
                ReadOnlyHint: annotations.ReadOnlyHint,
                DestructiveHint: annotations.DestructiveHint,
                IdempotentHint: annotations.IdempotentHint,
                OpenWorldHint: annotations.OpenWorldHint),
            MissingAnnotations: missing,
            Execution: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolTool, "Execution")),
            Icons: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolTool, "Icons")),
            Meta: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolTool, "Meta")),
            SchemaFingerprint: fingerprint);
    }

    private static IReadOnlyList<string> ComputeMissingAnnotations(ToolAnnotations? annotations)
    {
        var names = new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" };
        if (annotations is null)
        {
            return names;
        }

        var missing = new List<string>(names.Length);
        if (annotations.ReadOnlyHint is null) missing.Add("readOnlyHint");
        if (annotations.DestructiveHint is null) missing.Add("destructiveHint");
        if (annotations.IdempotentHint is null) missing.Add("idempotentHint");
        if (annotations.OpenWorldHint is null) missing.Add("openWorldHint");
        return missing;
    }

    private static SchemaFingerprint BuildSchemaFingerprint(JsonNode? inputSchema, JsonNode? outputSchema)
    {
        var inputBytes = inputSchema?.ToJsonString(AuthCheck.SerializerOptions).Length ?? 0;

        var parameterCount = 0;
        var requiredCount = 0;
        var maxDepth = inputSchema is null ? 0 : ComputeDepth(inputSchema);
        var hasAdditionalProperties = false;
        var usesOneOf = 0;
        var usesAnyOf = 0;
        var usesAllOf = 0;
        var usesRefOrDefs = false;
        var typeHistogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var formats = new List<string>();
        var parameterNames = new List<string>();

        if (inputSchema is JsonObject root)
        {
            if (root["properties"] is JsonObject properties)
            {
                parameterCount = properties.Count;
                foreach (var (name, value) in properties)
                {
                    parameterNames.Add(name);
                    if (value is JsonObject prop)
                    {
                        var typeNode = prop["type"];
                        var typeName = typeNode is JsonValue v && v.TryGetValue<string>(out var t) ? t : "unknown";
                        typeHistogram[typeName] = typeHistogram.TryGetValue(typeName, out var n) ? n + 1 : 1;
                        var format = prop["format"];
                        if (format is JsonValue fv && fv.TryGetValue<string>(out var fmt))
                        {
                            formats.Add(fmt);
                        }
                    }
                }
            }

            if (root["required"] is JsonArray reqArr)
            {
                requiredCount = reqArr.Count;
            }

            if (root["additionalProperties"] is JsonValue addV && addV.TryGetValue<bool>(out var addBool))
            {
                hasAdditionalProperties = addBool;
            }
            else if (root["additionalProperties"] is null)
            {
                // JSON Schema default is "true" when missing.
                hasAdditionalProperties = true;
            }

            usesOneOf = root["oneOf"] is JsonArray oneOfArr ? oneOfArr.Count : 0;
            usesAnyOf = root["anyOf"] is JsonArray anyOfArr ? anyOfArr.Count : 0;
            usesAllOf = root["allOf"] is JsonArray allOfArr ? allOfArr.Count : 0;
            usesRefOrDefs = inputSchema.ToJsonString().Contains("$ref", StringComparison.Ordinal)
                          || inputSchema.ToJsonString().Contains("$defs", StringComparison.Ordinal);
        }

        return new SchemaFingerprint(
            ParameterCount: parameterCount,
            RequiredCount: requiredCount,
            MaxNestingDepth: maxDepth,
            HasAdditionalProperties: hasAdditionalProperties,
            UsesOneOf: usesOneOf,
            UsesAnyOf: usesAnyOf,
            UsesAllOf: usesAllOf,
            UsesRefOrDefs: usesRefOrDefs,
            ParameterTypeHistogram: typeHistogram,
            ParameterFormats: formats,
            ParameterNames: parameterNames,
            SchemaBytes: inputBytes,
            HasOutputSchema: outputSchema is not null);
    }

    private static int ComputeDepth(JsonNode node, int depth = 0)
    {
        return node switch
        {
            JsonObject obj => obj.Count == 0 ? depth : obj.Max(kv => ComputeDepth(kv.Value!, depth + 1)),
            JsonArray arr => arr.Count == 0 ? depth : arr.Max(item => item is null ? depth : ComputeDepth(item, depth + 1)),
            _ => depth
        };
    }

    private static JsonNode? ToNode(object value) => CheckSessionHelpers.ToNode(value);

    internal sealed record ToolsData(
        bool Fetched,
        string? FetchedVia,
        string? FetchError,
        IReadOnlyList<ToolEntryExtended> Items);

    internal sealed record ToolEntryExtended(
        string Name,
        string? Title,
        string? Description,
        JsonNode? InputSchema,
        JsonNode? OutputSchema,
        ToolAnnotationsView? Annotations,
        IReadOnlyList<string> MissingAnnotations,
        JsonNode? Execution,
        JsonNode? Icons,
        JsonNode? Meta,
        SchemaFingerprint SchemaFingerprint);

    internal sealed record SchemaFingerprint(
        int ParameterCount,
        int RequiredCount,
        int MaxNestingDepth,
        bool HasAdditionalProperties,
        int UsesOneOf,
        int UsesAnyOf,
        int UsesAllOf,
        bool UsesRefOrDefs,
        IReadOnlyDictionary<string, int> ParameterTypeHistogram,
        IReadOnlyList<string> ParameterFormats,
        IReadOnlyList<string> ParameterNames,
        int SchemaBytes,
        bool HasOutputSchema);
}
