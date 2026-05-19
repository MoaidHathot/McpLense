using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpLense.Scanning;

/// <summary>
/// Loads <see cref="ScanConfig"/> from JSON. Same auto-discovery semantics as the old
/// profile loader: <c>$XDG_CONFIG_HOME/McpLense/McpLense.Config.json</c> on Unix and
/// <c>%APPDATA%/McpLense/McpLense.Config.json</c> on Windows. Multiple paths merge in order;
/// the LAST file wins on key conflicts within the <c>scan</c> block.
/// </summary>
internal static class ScanConfigLoader
{
    /// <summary>Canonical filename for the unified config (was: McpLense.Profiles.json).</summary>
    public const string ConfigFileName = "McpLense.Config.json";

    /// <summary>
    /// Loads a <see cref="ScanConfig"/> from one or more paths. Returns an empty config (all
    /// defaults) when no paths are supplied. Ignores files that don't contain a top-level
    /// <c>scan</c> block - this lets a user keep using a config file that only carries
    /// <c>authProfiles</c> without invoking any scan-config behaviour.
    /// </summary>
    public static async Task<ScanConfig> LoadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return new ScanConfig();
        }

        ScanConfig? merged = null;
        foreach (var path in paths)
        {
            var resolved = Path.GetFullPath(path);
            if (!File.Exists(resolved))
            {
                continue; // matches existing ProfileLoader-style "best effort" semantics
            }

            JsonNode? root;
            try
            {
                var text = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
                root = JsonNode.Parse(text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new UserInputException($"Failed to parse config file '{resolved}': {ex.Message}");
            }

            if (root is not JsonObject rootObj)
            {
                continue;
            }

            if (rootObj["scan"] is not JsonObject scanObj)
            {
                continue;
            }

            var entry = scanObj.Deserialize<ScanConfig>() ?? new ScanConfig();
            merged = merged is null ? entry : Merge(merged, entry);
        }

        return merged ?? new ScanConfig();
    }

    private static ScanConfig Merge(ScanConfig left, ScanConfig right)
    {
        // Last-write-wins on per-check entries: a downstream file can completely replace an
        // upstream entry without us trying to deep-merge the JsonObject contents. Keeps
        // semantics predictable when users layer configs.
        var checks = new Dictionary<string, JsonObject>(left.Checks, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in right.Checks)
        {
            checks[k] = v;
        }

        return new ScanConfig
        {
            Checks = checks,
            Output = new ScanOutputConfig
            {
                BaselineDir = right.Output.BaselineDir ?? left.Output.BaselineDir,
                Format = right.Output.Format ?? left.Output.Format
            },
            SchemaVersion = right.SchemaVersion != 0 ? right.SchemaVersion : left.SchemaVersion
        };
    }
}
