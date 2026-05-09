using System.Text.Json.Nodes;

namespace McpLense;

internal static class TargetResolver
{
    public static async Task<IReadOnlyList<ResolvedServer>> ResolveAsync(TargetOptions options, CancellationToken cancellationToken)
    {
        if (options.ConfigPath is not null)
        {
            return await ResolveFromConfigAsync(options, cancellationToken);
        }

        if (options.Url is not null)
        {
            return
            [
                new ResolvedServer(
                    Name: options.DisplayName ?? options.Url.Host,
                    Kind: ConnectionKind.Http,
                    Target: options.Url.ToString(),
                    Source: "direct-url",
                    Command: null,
                    CommandArguments: [],
                    WorkingDirectory: null,
                    Environment: new Dictionary<string, string>(),
                    Url: options.Url,
                    Transport: options.Transport,
                    Headers: new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase))
            ];
        }

        if (options.Command is not null)
        {
            return
            [
                new ResolvedServer(
                    Name: options.DisplayName ?? InferCommandName(options.Command),
                    Kind: ConnectionKind.Stdio,
                    Target: RenderCommandLine(options.Command, options.CommandArguments),
                    Source: "direct-stdio",
                    Command: options.Command,
                    CommandArguments: options.CommandArguments.ToArray(),
                    WorkingDirectory: options.WorkingDirectory,
                    Environment: new Dictionary<string, string>(options.Environment, StringComparer.OrdinalIgnoreCase),
                    Url: null,
                    Transport: TransportPreference.Auto,
                    Headers: new Dictionary<string, string>())
            ];
        }

        throw new UserInputException("No target was resolved.");
    }

    private static async Task<IReadOnlyList<ResolvedServer>> ResolveFromConfigAsync(TargetOptions options, CancellationToken cancellationToken)
    {
        var configPath = Path.GetFullPath(options.ConfigPath!);

        if (!File.Exists(configPath))
        {
            throw new UserInputException($"Config file '{configPath}' was not found.");
        }

        var text = await File.ReadAllTextAsync(configPath, cancellationToken);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            throw new UserInputException($"Failed to parse config JSON: {ex.Message}");
        }

        if (root is null)
        {
            throw new UserInputException("Config file is empty.");
        }

        var baseDirectory = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;
        var servers = ParseServers(root, baseDirectory);

        if (servers.Count == 0)
        {
            throw new UserInputException("No MCP servers were found in the config file.");
        }

        if (options.ServerNames.Count == 0)
        {
            return servers;
        }

        var names = new HashSet<string>(options.ServerNames, StringComparer.OrdinalIgnoreCase);
        var filtered = servers.Where(server => names.Contains(server.Name)).ToArray();

        if (filtered.Length == 0)
        {
            throw new UserInputException($"None of the requested servers were found: {string.Join(", ", options.ServerNames)}.");
        }

        return filtered;
    }

    private static List<ResolvedServer> ParseServers(JsonNode root, string baseDirectory)
    {
        var servers = new List<ResolvedServer>();

        switch (root)
        {
            case JsonObject obj:
                if (obj["mcpServers"] is JsonObject mcpServers)
                {
                    foreach (var entry in mcpServers)
                    {
                        if (entry.Value is JsonObject serverObject)
                        {
                            servers.Add(ParseServerDefinition(serverObject, entry.Key, baseDirectory));
                        }
                    }
                }

                if (obj["servers"] is JsonArray serverArray)
                {
                    foreach (var item in serverArray.OfType<JsonObject>())
                    {
                        servers.Add(ParseServerDefinition(item, GetString(item, "name"), baseDirectory));
                    }
                }

                if (obj["servers"] is JsonObject serverObjectMap)
                {
                    foreach (var entry in serverObjectMap)
                    {
                        if (entry.Value is JsonObject serverObject)
                        {
                            servers.Add(ParseServerDefinition(serverObject, entry.Key, baseDirectory));
                        }
                    }
                }

                if (servers.Count == 0 && LooksLikeServerDefinition(obj))
                {
                    servers.Add(ParseServerDefinition(obj, GetString(obj, "name") ?? "default", baseDirectory));
                }
                break;

            case JsonArray array:
                foreach (var item in array.OfType<JsonObject>())
                {
                    servers.Add(ParseServerDefinition(item, GetString(item, "name") ?? $"server-{servers.Count + 1}", baseDirectory));
                }
                break;
        }

        return servers;
    }

    private static bool LooksLikeServerDefinition(JsonObject obj)
        => obj.ContainsKey("command") || obj.ContainsKey("url") || obj.ContainsKey("endpoint");

    private static ResolvedServer ParseServerDefinition(JsonObject obj, string? nameHint, string baseDirectory)
    {
        var name = GetString(obj, "name") ?? nameHint ?? throw new UserInputException("Each server definition needs a name.");
        var command = GetString(obj, "command");
        var urlText = GetString(obj, "url") ?? GetString(obj, "endpoint");

        if (!string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(urlText))
        {
            throw new UserInputException($"Server '{name}' cannot define both a command and a URL.");
        }

        if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(urlText))
        {
            throw new UserInputException($"Server '{name}' must define either a command or a URL.");
        }

        if (!string.IsNullOrWhiteSpace(urlText))
        {
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
            {
                throw new UserInputException($"Server '{name}' has an invalid URL '{urlText}'.");
            }

            return new ResolvedServer(
                Name: name,
                Kind: ConnectionKind.Http,
                Target: uri.ToString(),
                Source: "config",
                Command: null,
                CommandArguments: [],
                WorkingDirectory: null,
                Environment: new Dictionary<string, string>(),
                Url: uri,
                Transport: ParseTransport(GetString(obj, "transport")),
                Headers: ParseObjectMap(obj["headers"] as JsonObject));
        }

        var arguments = ParseStringArray(obj["args"] as JsonArray);
        var workingDirectory = ResolvePath(baseDirectory, GetString(obj, "cwd") ?? GetString(obj, "workingDirectory"));

        return new ResolvedServer(
            Name: name,
            Kind: ConnectionKind.Stdio,
            Target: RenderCommandLine(command!, arguments),
            Source: "config",
            Command: command,
            CommandArguments: arguments,
            WorkingDirectory: workingDirectory,
            Environment: ParseObjectMap(obj["env"] as JsonObject),
            Url: null,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>());
    }

    private static TransportPreference ParseTransport(string? value) => value?.ToLowerInvariant() switch
    {
        null or "auto" => TransportPreference.Auto,
        "streamable-http" or "streamablehttp" or "http" => TransportPreference.StreamableHttp,
        "sse" => TransportPreference.Sse,
        _ => throw new UserInputException($"Unknown transport '{value}'.")
    };

    private static string ResolvePath(string baseDirectory, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null!;
        }

        return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(baseDirectory, value));
    }

    private static string InferCommandName(string command)
    {
        var fileName = Path.GetFileNameWithoutExtension(command);
        return string.IsNullOrWhiteSpace(fileName) ? command : fileName;
    }

    private static string RenderCommandLine(string command, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { Quote(command) };
        parts.AddRange(arguments.Select(Quote));
        return string.Join(" ", parts);
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static string? GetString(JsonObject obj, string propertyName)
        => obj[propertyName]?.GetValue<string>();

    private static IReadOnlyList<string> ParseStringArray(JsonArray? array)
    {
        if (array is null)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            if (item is not null)
            {
                values.Add(item.GetValue<string>());
            }
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> ParseObjectMap(JsonObject? obj)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (obj is null)
        {
            return values;
        }

        foreach (var entry in obj)
        {
            values[entry.Key] = entry.Value?.ToJsonString() switch
            {
                null => string.Empty,
                _ when entry.Value is JsonValue => entry.Value!.GetValue<string>(),
                var serialized => serialized
            };
        }

        return values;
    }
}
