using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Computes per-item content hashes (each tool / prompt / resource gets a stable
/// <c>contentHash</c>) and a top-level <c>serverFingerprint</c> over the canonical
/// serialisation of all stable check outputs. Used by the diff engine to detect drift
/// without doing a deep JSON compare.
/// </summary>
internal sealed class HashingCheck : IScanCheck
{
    public string Id => "hashing";

    /// <summary>
    /// Reads everything stable. Excludes <c>timings</c> (intentionally not in this graph -
    /// the pipeline already separates timings into the server-level <c>timings</c> block).
    /// </summary>
    public IReadOnlyList<string> DependsOn => new[] { "auth", "tools", "prompts", "resources", "protocol", "serverInfo" };
    public bool IsEnabledByDefault => true;

    public Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        // The pipeline already wrote per-check outputs into PriorOutputs. We mutate copies
        // here to add `contentHash` to each tool/prompt/resource entry, then compute the
        // server-level fingerprint over the resulting structure.
        var toolHashes = HashItems(context, "tools", "items", "name");
        var promptHashes = HashItems(context, "prompts", "items", "name");
        var resourceHashes = HashItems(context, "resources", "items", "uri");

        // Server fingerprint = SHA256 of canonical JSON of all stable check outputs.
        var canonical = new JsonObject
        {
            ["auth"] = CanonicaliseAuth(context.GetPriorOutput("auth")),
            ["serverInfo"] = context.GetPriorOutput("serverInfo")?.DeepClone(),
            ["protocol"] = context.GetPriorOutput("protocol")?.DeepClone(),
            ["tools"] = context.GetPriorOutput("tools")?.DeepClone(),
            ["prompts"] = context.GetPriorOutput("prompts")?.DeepClone(),
            ["resources"] = context.GetPriorOutput("resources")?.DeepClone()
        };
        var fingerprint = Hash(CanonicalJson(canonical));

        var data = new HashingData(
            Algorithm: "sha256",
            ServerFingerprint: fingerprint,
            ToolHashes: toolHashes,
            PromptHashes: promptHashes,
            ResourceHashes: resourceHashes);

        return Task.FromResult(new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(data), Error: null));
    }

    private static IReadOnlyDictionary<string, string> HashItems(ScanContext context, string checkId, string arrayProp, string idProp)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (context.GetPriorOutput(checkId) is not JsonObject obj)
        {
            return result;
        }

        if (obj[arrayProp] is not JsonArray array)
        {
            return result;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            var id = item[idProp]?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var canonical = CanonicalJson(item);
            result[id] = Hash(canonical);
        }

        return result;
    }

    private static JsonNode? CanonicaliseAuth(JsonNode? authNode)
    {
        // Strip details that are reasonable to vary between scans (status code on transient
        // 503s, mostly): keep classification, RFC 9728 metadata, profile attempts (without
        // duration / detail strings).
        if (authNode is not JsonObject obj)
        {
            return null;
        }

        return new JsonObject
        {
            ["classification"] = obj["classification"]?.DeepClone(),
            ["summary"] = obj["summary"]?.DeepClone(),
            ["details"] = obj["details"]?.DeepClone()
        };
    }

    /// <summary>Stable JSON: sorted property names, no whitespace.</summary>
    public static string CanonicalJson(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        var sorted = Sort(node);
        return sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonNode Sort(JsonNode node)
    {
        return node switch
        {
            JsonObject obj => new JsonObject(obj
                .Where(kv => kv.Value is not null)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Sort(kv.Value!)))),
            JsonArray array => new JsonArray(array.Select(item => item is null ? null : Sort(item)).ToArray()!),
            _ => node.DeepClone()
        };
    }

    public static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal sealed record HashingData(
        string Algorithm,
        string ServerFingerprint,
        IReadOnlyDictionary<string, string> ToolHashes,
        IReadOnlyDictionary<string, string> PromptHashes,
        IReadOnlyDictionary<string, string> ResourceHashes);
}
