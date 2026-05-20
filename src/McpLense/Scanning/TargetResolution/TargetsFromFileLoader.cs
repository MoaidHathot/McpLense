namespace McpLense.Scanning.TargetResolution;

/// <summary>
/// Reads <c>--targets-from</c> files: plain-text lists of MCP targets, one per line. Each
/// line is either an absolute http(s) URL or an <c>@name</c> reference to a target declared
/// in <see cref="ScanConfig.Targets"/>. Blank lines and lines starting with <c>#</c> are
/// ignored so consumers can keep their lists annotated.
/// </summary>
/// <remarks>
/// Fleet-scale consumers (1000+ MCPs) want to hand McpLense the full target list and let it
/// own the parallelism instead of forking N processes that each pay the .NET / scanner
/// startup tax. This loader is the file-list side of that contract; <c>--parallel-servers</c>
/// is the concurrency side.
/// </remarks>
internal static class TargetsFromFileLoader
{
    public static async Task<IReadOnlyList<ResolvedServer>> LoadAsync(
        IReadOnlyList<string> paths,
        ScanConfig scanConfig,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(scanConfig);

        if (paths.Count == 0)
        {
            return [];
        }

        // De-duplicate by target string so two list files that overlap don't fire the same
        // scan twice. URLs are normalised through Uri so `https://a/` and `https://a` collapse
        // sensibly; @name references stay verbatim because their identity is the name.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ResolvedServer>();
        var namedTargets = scanConfig.Targets
            .Where(t => !string.IsNullOrEmpty(t.Name))
            .ToDictionary(t => t.Name!, StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in paths)
        {
            var path = Path.GetFullPath(rawPath);
            if (!File.Exists(path))
            {
                throw new UserInputException($"--targets-from file '{path}' was not found.");
            }

            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                var line = rawLine.Trim();
                if (line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith('@'))
                {
                    var name = line[1..];
                    if (string.IsNullOrEmpty(name))
                    {
                        throw new UserInputException(
                            $"--targets-from: '{path}' line {i + 1}: '@' must be followed by a target name.");
                    }

                    if (!namedTargets.TryGetValue(name, out var entry) || string.IsNullOrEmpty(entry.Url))
                    {
                        throw new UserInputException(
                            $"--targets-from: '{path}' line {i + 1}: named target '@{name}' is not defined in the loaded McpLense.Config.json.");
                    }

                    var identity = $"@{name}";
                    if (!seen.Add(identity))
                    {
                        continue;
                    }

                    if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var entryUri))
                    {
                        throw new UserInputException(
                            $"--targets-from: '{path}' line {i + 1}: named target '@{name}' has an invalid URL '{entry.Url}'.");
                    }

                    resolved.Add(BuildHttpServer(entry.Name ?? entryUri.Host, entryUri, source: $"targets-from:{Path.GetFileName(path)}#{i + 1}"));
                    continue;
                }

                if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new UserInputException(
                        $"--targets-from: '{path}' line {i + 1}: '{line}' is not an absolute http(s) URL or '@name' reference.");
                }

                var key = uri.ToString();
                if (!seen.Add(key))
                {
                    continue;
                }

                resolved.Add(BuildHttpServer(uri.Host, uri, source: $"targets-from:{Path.GetFileName(path)}#{i + 1}"));
            }
        }

        return resolved;
    }

    private static ResolvedServer BuildHttpServer(string name, Uri url, string source)
        => new(
            Name: name,
            Kind: ConnectionKind.Http,
            Target: url.ToString(),
            Source: source,
            Command: null,
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: url,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
