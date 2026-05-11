using System.Runtime.InteropServices;

namespace McpLense;

/// <summary>
/// Resolves the platform-specific search paths McpLense uses to auto-discover profile files.
///
/// Resolution order (first hit wins):
/// <list type="number">
///   <item><c>$XDG_CONFIG_HOME/McpLense</c> when <c>XDG_CONFIG_HOME</c> is set on any OS.</item>
///   <item>Windows fallback: <c>%APPDATA%\McpLense</c>.</item>
///   <item>Unix fallback: <c>~/.config/McpLense</c>.</item>
/// </list>
///
/// Within the chosen directory, McpLense loads BOTH a single root file
/// (<see cref="ProfilesFileName"/>) AND every <c>*.json</c> entry under <see cref="ProfilesSubdirectoryName"/>.
/// </summary>
internal static class DefaultConfigPaths
{
    /// <summary>Canonical filename for the single-file profile config.</summary>
    public const string ProfilesFileName = "McpLense.Profiles.json";

    /// <summary>Subdirectory name beneath the McpLense config root that holds split profile files.</summary>
    public const string ProfilesSubdirectoryName = "profiles";

    /// <summary>
    /// Resolves the McpLense config root for the current process. Honors
    /// <c>$XDG_CONFIG_HOME</c> on every platform; otherwise picks the OS-native fallback.
    /// </summary>
    /// <returns>
    /// Absolute path to the config root, or <c>null</c> when no platform-appropriate location
    /// can be determined (e.g. the home directory cannot be resolved).
    /// </returns>
    public static string? ResolveRoot()
        => ResolveRoot(Environment.GetEnvironmentVariable, RuntimeInformation.IsOSPlatform);

    /// <summary>For tests: inject custom env-var lookup and OS detection.</summary>
    internal static string? ResolveRoot(Func<string, string?> env, Func<OSPlatform, bool> isOsPlatform)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(isOsPlatform);

        var xdg = env("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "McpLense");
        }

        if (isOsPlatform(OSPlatform.Windows))
        {
            var appData = env("APPDATA");
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return Path.Combine(appData, "McpLense");
            }

            return null;
        }

        // Unix fallback (Linux, macOS, *BSD): ~/.config/McpLense per XDG Base Directory Spec.
        var home = env("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        return Path.Combine(home, ".config", "McpLense");
    }

    /// <summary>
    /// Returns every profile file path that exists under the supplied root, in deterministic
    /// order: the root <see cref="ProfilesFileName"/> first (when present), then alphabetised
    /// <c>*.json</c> entries from the <see cref="ProfilesSubdirectoryName"/> subdirectory.
    /// </summary>
    /// <param name="root">Config root resolved via <see cref="ResolveRoot()"/>; may be null.</param>
    /// <returns>
    /// Empty list when <paramref name="root"/> is null/empty or no matching files exist. Never
    /// throws for missing files — auto-discovery is best-effort by design.
    /// </returns>
    public static IReadOnlyList<string> EnumerateProfileFiles(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return [];
        }

        var paths = new List<string>();

        var rootFile = Path.Combine(root, ProfilesFileName);
        if (File.Exists(rootFile))
        {
            paths.Add(rootFile);
        }

        var subDir = Path.Combine(root, ProfilesSubdirectoryName);
        if (Directory.Exists(subDir))
        {
            // Sort to keep load order deterministic across machines/filesystems. Otherwise
            // duplicate-name conflicts could surface non-deterministically when two split
            // profile files re-use the same name.
            var jsonFiles = Directory.GetFiles(subDir, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(jsonFiles, StringComparer.OrdinalIgnoreCase);
            paths.AddRange(jsonFiles);
        }

        return paths;
    }
}
