using System.Text.Json;
using System.Text.Json.Nodes;
using McpLense.Scanning.TargetResolution;

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
    /// defaults) when no paths are supplied. Reads the <c>scan</c> block AND the top-level
    /// <c>targets</c> / <c>targetPatterns</c> blocks (the latter sit at the root for parity
    /// with <c>authProfiles</c> so the user doesn't have to nest them under <c>scan</c>).
    /// </summary>
    public static async Task<ScanConfig> LoadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return new ScanConfig();
        }

        var expander = new EnvironmentExpander();
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

            ScanConfig entry;
            if (rootObj["scan"] is JsonObject scanObj)
            {
                entry = scanObj.Deserialize<ScanConfig>() ?? new ScanConfig();
            }
            else
            {
                entry = new ScanConfig();
            }

            // Top-level targets / targetPatterns (peer of authProfiles + scan). We accept the
            // legacy nested location (under "scan") too via the deserialise above, but the
            // recommended layout is top-level so they stay alongside authProfiles.
            if (rootObj["targets"] is JsonArray topTargets)
            {
                ApplyTargets(entry.Targets, topTargets, expander, resolved);
            }

            if (rootObj["targetPatterns"] is JsonArray topPatterns)
            {
                ApplyPatterns(entry.TargetPatterns, topPatterns, expander, resolved);
            }

            // Env-expand the nested copy too (Deserialize<> doesn't run the expander).
            ExpandHeadersInPlace(entry.Targets, entry.TargetPatterns, expander);

            merged = merged is null ? entry : Merge(merged, entry);
        }

        return merged ?? new ScanConfig();
    }

    private static void ApplyTargets(List<ScanTargetEntry> destination, JsonArray items, EnvironmentExpander expander, string sourcePath)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject obj)
            {
                continue;
            }

            var entry = obj.Deserialize<ScanTargetEntry>();
            if (entry is null)
            {
                continue;
            }

            destination.Add(ExpandTarget(entry, expander, $"{sourcePath}#targets[{index}]"));
        }
    }

    private static void ApplyPatterns(List<TargetPatternEntry> destination, JsonArray items, EnvironmentExpander expander, string sourcePath)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject obj)
            {
                continue;
            }

            var entry = obj.Deserialize<TargetPatternEntry>();
            if (entry is null)
            {
                continue;
            }

            // Compile-validate the pattern up-front so a typo doesn't silently lose coverage.
            if (!string.IsNullOrEmpty(entry.Match) && !UrlGlob.TryCompile(entry.Match!, out _, out var error))
            {
                Console.Error.WriteLine($"warning: {sourcePath}#targetPatterns[{index}].match invalid: {error}; ignored.");
                continue;
            }

            destination.Add(ExpandPattern(entry, expander, $"{sourcePath}#targetPatterns[{index}]"));
        }
    }

    private static ScanTargetEntry ExpandTarget(ScanTargetEntry entry, EnvironmentExpander expander, string contextPath)
    {
        return new ScanTargetEntry
        {
            Name = entry.Name,
            Url = string.IsNullOrEmpty(entry.Url) ? entry.Url : expander.Expand(entry.Url, $"{contextPath}.url"),
            Headers = ExpandMap(entry.Headers, expander, $"{contextPath}.headers"),
            Scope = entry.Scope,
            Profile = string.IsNullOrEmpty(entry.Profile) ? entry.Profile : expander.Expand(entry.Profile, $"{contextPath}.profile"),
            Transport = entry.Transport,
            TimeoutSeconds = entry.TimeoutSeconds,
            DisabledChecks = entry.DisabledChecks
        };
    }

    private static TargetPatternEntry ExpandPattern(TargetPatternEntry entry, EnvironmentExpander expander, string contextPath)
    {
        return new TargetPatternEntry
        {
            Match = entry.Match,
            Headers = ExpandMap(entry.Headers, expander, $"{contextPath}.headers"),
            Scope = entry.Scope,
            Profile = string.IsNullOrEmpty(entry.Profile) ? entry.Profile : expander.Expand(entry.Profile, $"{contextPath}.profile"),
            Transport = entry.Transport,
            TimeoutSeconds = entry.TimeoutSeconds,
            DisabledChecks = entry.DisabledChecks
        };
    }

    private static Dictionary<string, string>? ExpandMap(Dictionary<string, string>? input, EnvironmentExpander expander, string contextPath)
    {
        if (input is null || input.Count == 0)
        {
            return input;
        }

        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in input)
        {
            output[key] = string.IsNullOrEmpty(value) ? value : expander.Expand(value, $"{contextPath}.{key}");
        }

        return output;
    }

    private static void ExpandHeadersInPlace(
        IReadOnlyList<ScanTargetEntry> targets,
        IReadOnlyList<TargetPatternEntry> patterns,
        EnvironmentExpander expander)
    {
        // Deserialize<> bypassed the expander above; sweep one more time for entries that
        // came from the nested location.
        for (var i = 0; i < targets.Count; i++)
        {
            var entry = targets[i];
            if (entry.Headers is null)
            {
                continue;
            }

            var expanded = ExpandMap(entry.Headers, expander, $"scan.targets[{i}].headers");
            // ScanTargetEntry is immutable; replacing the dictionary contents in-place via
            // the existing one keeps the reference stable.
            if (expanded is not null && !ReferenceEquals(expanded, entry.Headers))
            {
                entry.Headers.Clear();
                foreach (var (k, v) in expanded)
                {
                    entry.Headers[k] = v;
                }
            }
        }

        for (var i = 0; i < patterns.Count; i++)
        {
            var entry = patterns[i];
            if (entry.Headers is null)
            {
                continue;
            }

            var expanded = ExpandMap(entry.Headers, expander, $"scan.targetPatterns[{i}].headers");
            if (expanded is not null && !ReferenceEquals(expanded, entry.Headers))
            {
                entry.Headers.Clear();
                foreach (var (k, v) in expanded)
                {
                    entry.Headers[k] = v;
                }
            }
        }
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

        // Targets + patterns are append (left-to-right). Duplicate target NAMES across files
        // are an error - same convention as auth profiles.
        var targets = new List<ScanTargetEntry>(left.Targets);
        var seenNames = new HashSet<string>(
            left.Targets.Where(t => !string.IsNullOrEmpty(t.Name)).Select(t => t.Name!),
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in right.Targets)
        {
            if (!string.IsNullOrEmpty(entry.Name) && !seenNames.Add(entry.Name!))
            {
                throw new UserInputException($"Duplicate target name '{entry.Name}' across config files.");
            }
            targets.Add(entry);
        }

        var patterns = new List<TargetPatternEntry>(left.TargetPatterns);
        patterns.AddRange(right.TargetPatterns);

        return new ScanConfig
        {
            Checks = checks,
            Output = new ScanOutputConfig
            {
                BaselineDir = right.Output.BaselineDir ?? left.Output.BaselineDir,
                Format = right.Output.Format ?? left.Output.Format
            },
            Targets = targets,
            TargetPatterns = patterns,
            SchemaVersion = right.SchemaVersion != 0 ? right.SchemaVersion : left.SchemaVersion
        };
    }
}
