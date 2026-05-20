using System.Text.Json.Nodes;

namespace McpLense;

/// <summary>
/// Loads and merges <see cref="AuthProfile"/> entries from one or more profile config files.
/// Performs duplicate-name detection across the merged set and rejects files that look like
/// stdio configs (i.e. that contain a <c>servers</c> or <c>mcpServers</c> block).
///
/// <para>
/// Library hosts that want to drive McpLense's scan pipeline in-process can use
/// <see cref="LoadFromXdgAsync"/> / <see cref="LoadFromFileAsync"/> to produce the exact
/// same <c>IReadOnlyList&lt;AuthProfile&gt;</c> the CLI would have loaded - including
/// <c>env:VAR</c> and <c>${VAR:-default}</c> expansion - without having to reimplement the
/// XDG/APPDATA discovery rules or the bash-style env-expansion semantics.
/// </para>
/// </summary>
public static class ProfileLoader
{
    /// <summary>
    /// Reads every supplied path, parses <c>authProfiles</c>, and merges the results.
    /// Duplicate names across files raise a <see cref="UserInputException"/> with both source
    /// paths shown so the user knows which file to edit.
    /// </summary>
    /// <param name="paths">Files to load (in order). Empty list returns an empty result.</param>
    /// <param name="expander">
    /// Variable expander applied to every string field. Pass a custom instance to expand
    /// against a sealed secrets map instead of the process environment.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Loads profiles from a single file. Convenience wrapper around <see cref="LoadAsync"/>
    /// using a default <see cref="EnvironmentExpander"/> bound to the process environment.
    /// </summary>
    /// <param name="path">Absolute or relative path to a profile JSON file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<IReadOnlyList<AuthProfile>> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
        => LoadFromFileAsync(path, new EnvironmentExpander(), cancellationToken);

    /// <summary>
    /// Loads profiles from a single file with a caller-supplied <see cref="EnvironmentExpander"/>.
    /// </summary>
    public static Task<IReadOnlyList<AuthProfile>> LoadFromFileAsync(string path, EnvironmentExpander expander, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadAsync(new[] { path }, expander, cancellationToken);
    }

    /// <summary>
    /// Loads profiles from the platform-default discovery locations (XDG/APPDATA/HOME). This
    /// is exactly what the CLI does when no <c>--profiles</c> flag is supplied. Honours the
    /// <c>MCPLENSE_NO_PROFILE_AUTO_DISCOVERY</c> kill-switch.
    /// </summary>
    /// <remarks>
    /// Resolution order: <c>$XDG_CONFIG_HOME/McpLense</c> wins on any OS when set; otherwise
    /// <c>%APPDATA%\McpLense</c> on Windows and <c>~/.config/McpLense</c> on Unix. Within
    /// that root, both <c>McpLense.Profiles.json</c> AND every <c>profiles/*.json</c> entry
    /// are loaded (deterministic order: root file first, then alphabetised subdir entries).
    /// Returns an empty list when no profile files exist or auto-discovery is disabled.
    /// </remarks>
    public static Task<IReadOnlyList<AuthProfile>> LoadFromXdgAsync(CancellationToken cancellationToken = default)
        => LoadFromXdgAsync(new EnvironmentExpander(), cancellationToken);

    /// <summary>
    /// Loads profiles from the platform-default discovery locations with a caller-supplied
    /// <see cref="EnvironmentExpander"/>.
    /// </summary>
    public static Task<IReadOnlyList<AuthProfile>> LoadFromXdgAsync(EnvironmentExpander expander, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expander);
        var root = DefaultConfigPaths.ResolveRoot();
        var paths = DefaultConfigPaths.EnumerateProfileFiles(root);
        return LoadAsync(paths, expander, cancellationToken);
    }
}
