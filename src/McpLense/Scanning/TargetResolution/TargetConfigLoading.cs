namespace McpLense.Scanning.TargetResolution;

/// <summary>
/// Shared helper for discovering the set of config-file paths that hold profiles AND
/// targets / target patterns. Replaces two near-identical copies that used to live in
/// <see cref="ScanCommandDispatcher"/> and <c>McpExecutor</c>; consolidating them here
/// guarantees both code paths see the same auto-discovered files.
/// </summary>
internal static class TargetConfigLoading
{
    /// <summary>
    /// Returns the path list to feed to <see cref="ScanConfigLoader.LoadAsync"/> /
    /// <see cref="ProfileLoader.LoadAsync"/>. When explicit CLI <c>--profiles</c> paths
    /// are present they win outright. Otherwise auto-discover from
    /// <see cref="DefaultConfigPaths"/> AND, when present, the unified
    /// <c>McpLense.Config.json</c> file under the same root.
    /// </summary>
    public static IReadOnlyList<string> ResolveScanConfigPaths(IReadOnlyList<string> explicitPaths)
    {
        ArgumentNullException.ThrowIfNull(explicitPaths);

        if (explicitPaths.Count > 0)
        {
            return explicitPaths;
        }

        var root = DefaultConfigPaths.ResolveRoot();
        var discovered = DefaultConfigPaths.EnumerateProfileFiles(root);

        // Also discover the unified config file - the user may have renamed
        // McpLense.Profiles.json -> McpLense.Config.json. We accept either name.
        if (root is not null)
        {
            var configFile = Path.Combine(root, ScanConfigLoader.ConfigFileName);
            if (File.Exists(configFile) && !discovered.Contains(configFile))
            {
                var merged = new List<string>(discovered) { configFile };
                return merged;
            }
        }

        return discovered;
    }
}
