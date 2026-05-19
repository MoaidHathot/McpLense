using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpLense.Scanning;

/// <summary>
/// Reads / writes <see cref="ScanReport"/> JSON files on disk under
/// <c>&lt;baselineDir&gt;/&lt;host&gt;/&lt;UTC-timestamp&gt;.json</c> as decided per
/// design. Default <c>baselineDir</c> is the process working directory; CLI <c>--baseline
/// &lt;path&gt;</c> overrides; config-file <c>scan.output.baselineDir</c> is the middle
/// option.
/// </summary>
internal static class BaselineWriter
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>
    /// Resolves the on-disk path to write a baseline to. <paramref name="cliPath"/> wins
    /// (if it's a file path, used directly; if it's a directory, append host+timestamp).
    /// Otherwise falls back to <paramref name="configDir"/>; otherwise the current directory.
    /// </summary>
    public static string ResolvePath(string? cliPath, string? configDir, ScanReport report)
    {
        if (!string.IsNullOrEmpty(cliPath))
        {
            // If cliPath looks like a directory (existing dir, or ends with separator), treat
            // as one. Otherwise treat as the full target file path - user wants exact control.
            if (Directory.Exists(cliPath)
                || cliPath.EndsWith(Path.DirectorySeparatorChar)
                || cliPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                return ResolveAutoPath(cliPath, report);
            }

            return cliPath;
        }

        if (!string.IsNullOrEmpty(configDir))
        {
            return ResolveAutoPath(configDir, report);
        }

        return ResolveAutoPath(Environment.CurrentDirectory, report);
    }

    private static string ResolveAutoPath(string baseDir, ScanReport report)
    {
        // Pick the first server's host as the path segment. Multi-server reports use the
        // first server's host (good enough for catalogue-style baselines; users naming
        // multi-server scans can pass --baseline explicitly).
        var host = report.Servers.FirstOrDefault()?.Target;
        if (host is not null && Uri.TryCreate(host, UriKind.Absolute, out var url))
        {
            host = url.Host;
        }
        else
        {
            host = "scan";
        }

        var timestamp = report.GeneratedAt.ToString("yyyyMMddTHHmmssZ");
        return Path.Combine(baseDir, host, $"{timestamp}.json");
    }

    public static async Task WriteAsync(string path, ScanReport report, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ScanReport> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new UserInputException($"Baseline file '{fullPath}' was not found.");
        }

        await using var stream = File.OpenRead(fullPath);
        var report = await JsonSerializer.DeserializeAsync<ScanReport>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            throw new UserInputException($"Baseline file '{fullPath}' did not deserialize to a ScanReport.");
        }

        return report;
    }
}
