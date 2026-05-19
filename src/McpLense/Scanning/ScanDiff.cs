using System.Text.Json;
using System.Text.Json.Nodes;
using McpLense.Scanning.Checks;

namespace McpLense.Scanning;

/// <summary>
/// Pure-JSON structural diff between two <see cref="ScanReport"/>s. Identity rules:
/// servers by <c>target</c>; tools / prompts / resources within a server by their natural
/// id (name / uri). Uses hash-equality on <c>contentHash</c> from <see cref="HashingCheck"/>
/// when available to short-circuit deep compare.
/// </summary>
internal static class ScanDiff
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static ScanDiffReport Diff(ScanReport before, ScanReport after)
    {
        var beforeByTarget = before.Servers.ToDictionary(s => s.Target, s => s, StringComparer.OrdinalIgnoreCase);
        var afterByTarget = after.Servers.ToDictionary(s => s.Target, s => s, StringComparer.OrdinalIgnoreCase);

        var serverDiffs = new List<ServerDiff>();
        foreach (var (target, afterEntry) in afterByTarget)
        {
            if (!beforeByTarget.TryGetValue(target, out var beforeEntry))
            {
                serverDiffs.Add(new ServerDiff(
                    Target: target,
                    Name: afterEntry.Name,
                    Status: "added",
                    ServerFingerprintBefore: null,
                    ServerFingerprintAfter: GetFingerprint(afterEntry),
                    Checks: new Dictionary<string, JsonNode?>()));
                continue;
            }

            var fingerprintBefore = GetFingerprint(beforeEntry);
            var fingerprintAfter = GetFingerprint(afterEntry);
            if (string.Equals(fingerprintBefore, fingerprintAfter, StringComparison.Ordinal) && fingerprintBefore is not null)
            {
                continue; // unchanged: skip
            }

            serverDiffs.Add(new ServerDiff(
                Target: target,
                Name: afterEntry.Name,
                Status: "changed",
                ServerFingerprintBefore: fingerprintBefore,
                ServerFingerprintAfter: fingerprintAfter,
                Checks: DiffChecks(beforeEntry.Checks, afterEntry.Checks)));
        }

        foreach (var (target, beforeEntry) in beforeByTarget)
        {
            if (!afterByTarget.ContainsKey(target))
            {
                serverDiffs.Add(new ServerDiff(
                    Target: target,
                    Name: beforeEntry.Name,
                    Status: "removed",
                    ServerFingerprintBefore: GetFingerprint(beforeEntry),
                    ServerFingerprintAfter: null,
                    Checks: new Dictionary<string, JsonNode?>()));
            }
        }

        return new ScanDiffReport(
            BaselineGeneratedAt: before.GeneratedAt,
            CurrentGeneratedAt: after.GeneratedAt,
            Servers: serverDiffs);
    }

    private static IReadOnlyDictionary<string, JsonNode?> DiffChecks(
        IReadOnlyDictionary<string, JsonNode?> before,
        IReadOnlyDictionary<string, JsonNode?> after)
    {
        var diffs = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var allKeys = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
        allKeys.UnionWith(after.Keys);

        foreach (var key in allKeys)
        {
            var beforeNode = before.TryGetValue(key, out var b) ? b : null;
            var afterNode = after.TryGetValue(key, out var a) ? a : null;

            if (IsEqual(beforeNode, afterNode))
            {
                continue;
            }

            // Special-case the "tools" / "prompts" / "resources" arrays: produce
            // added / removed / changed sub-arrays keyed by natural id.
            if (key.Equals("tools", StringComparison.OrdinalIgnoreCase))
            {
                diffs[key] = DiffArray(beforeNode, afterNode, "items", "name", "contentHash");
            }
            else if (key.Equals("prompts", StringComparison.OrdinalIgnoreCase))
            {
                diffs[key] = DiffArray(beforeNode, afterNode, "items", "name", "contentHash");
            }
            else if (key.Equals("resources", StringComparison.OrdinalIgnoreCase))
            {
                diffs[key] = DiffArray(beforeNode, afterNode, "items", "uri", "contentHash");
            }
            else
            {
                diffs[key] = new JsonObject
                {
                    ["before"] = beforeNode?.DeepClone(),
                    ["after"] = afterNode?.DeepClone()
                };
            }
        }

        return diffs;
    }

    private static JsonNode? DiffArray(JsonNode? before, JsonNode? after, string arrayProp, string idProp, string hashProp)
    {
        var beforeItems = ExtractItems(before, arrayProp, idProp);
        var afterItems = ExtractItems(after, arrayProp, idProp);

        var added = new JsonArray();
        var removed = new JsonArray();
        var changed = new JsonArray();

        foreach (var (id, afterItem) in afterItems)
        {
            if (!beforeItems.TryGetValue(id, out var beforeItem))
            {
                added.Add(afterItem.DeepClone());
                continue;
            }

            if (IsEqual(beforeItem, afterItem))
            {
                continue;
            }

            changed.Add(new JsonObject
            {
                ["id"] = id,
                ["before"] = beforeItem.DeepClone(),
                ["after"] = afterItem.DeepClone()
            });
        }

        foreach (var (id, beforeItem) in beforeItems)
        {
            if (!afterItems.ContainsKey(id))
            {
                removed.Add(beforeItem.DeepClone());
            }
        }

        return new JsonObject
        {
            ["added"] = added,
            ["removed"] = removed,
            ["changed"] = changed
        };
    }

    private static Dictionary<string, JsonObject> ExtractItems(JsonNode? node, string arrayProp, string idProp)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject obj || obj[arrayProp] is not JsonArray arr)
        {
            return result;
        }

        foreach (var item in arr.OfType<JsonObject>())
        {
            var id = item[idProp]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id))
            {
                result[id] = item;
            }
        }

        return result;
    }

    private static string? GetFingerprint(ServerScanResult server)
    {
        if (!server.Checks.TryGetValue("hashing", out var hashing) || hashing is not JsonObject obj)
        {
            return null;
        }

        return obj["serverFingerprint"]?.GetValue<string>();
    }

    /// <summary>
    /// Order-insensitive deep JSON comparison via canonical-JSON hashing. Two nodes are
    /// equal iff their canonicalised forms produce the same SHA-256.
    /// </summary>
    private static bool IsEqual(JsonNode? a, JsonNode? b)
        => HashingCheck.CanonicalJson(a) == HashingCheck.CanonicalJson(b);

    public static string Serialize(ScanDiffReport diff)
        => JsonSerializer.Serialize(diff, JsonOptions);

    internal sealed record ScanDiffReport(
        DateTimeOffset BaselineGeneratedAt,
        DateTimeOffset CurrentGeneratedAt,
        IReadOnlyList<ServerDiff> Servers);

    internal sealed record ServerDiff(
        string Target,
        string Name,
        string Status,
        string? ServerFingerprintBefore,
        string? ServerFingerprintAfter,
        IReadOnlyDictionary<string, JsonNode?> Checks);
}
