using System.Text.Json.Nodes;

namespace McpLense;

/// <summary>
/// Loads and merges <see cref="AuthProfile"/> entries from one or more profile config files.
/// Performs duplicate-name detection across the merged set and rejects files that look like
/// stdio configs (i.e. that contain a <c>servers</c> or <c>mcpServers</c> block).
/// </summary>
internal static class ProfileLoader
{
    /// <summary>
    /// Reads every supplied path, parses <c>authProfiles</c>, and merges the results.
    /// Duplicate names across files raise a <see cref="UserInputException"/> with both source
    /// paths shown so the user knows which file to edit.
    /// </summary>
    public static async Task<IReadOnlyList<AuthProfile>> LoadAsync(IReadOnlyList<string> paths, EnvironmentExpander expander, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(expander);

        if (paths.Count == 0)
        {
            return [];
        }

        var parser = new AuthConfigParser(expander);
        var merged = new List<AuthProfile>();
        var sourceByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in paths)
        {
            var path = Path.GetFullPath(rawPath);
            if (!File.Exists(path))
            {
                throw new UserInputException($"Profile file '{path}' was not found.");
            }

            JsonNode? root;
            try
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                root = JsonNode.Parse(text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new UserInputException($"Failed to parse profile file '{path}': {ex.Message}");
            }

            if (root is not JsonObject obj)
            {
                throw new UserInputException($"Profile file '{path}' must contain a JSON object at the root.");
            }

            // A profile file MUST NOT contain server definitions; if it does the user almost
            // certainly mixed up --profiles with --config. Surface that mistake loudly.
            if (obj.ContainsKey("servers") || obj.ContainsKey("mcpServers"))
            {
                throw new UserInputException(
                    $"Profile file '{path}' contains a 'servers'/'mcpServers' block. " +
                    "Profile files only hold 'authProfiles'; pass stdio server definitions via --config instead.");
            }

            var profiles = parser.ParseAuthProfiles(obj);
            foreach (var profile in profiles)
            {
                if (sourceByName.TryGetValue(profile.Name, out var existingPath))
                {
                    throw new UserInputException(
                        $"Duplicate auth profile name '{profile.Name}' (defined in '{existingPath}' and '{path}'). " +
                        "Rename one of them.");
                }

                sourceByName[profile.Name] = path;
                merged.Add(profile);
            }
        }

        return merged;
    }
}
