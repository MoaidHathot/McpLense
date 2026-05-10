using System.Text.Json.Nodes;

namespace McpLense;

internal static class TargetResolver
{
    public static Task<IReadOnlyList<ResolvedServer>> ResolveAsync(TargetOptions options, CancellationToken cancellationToken)
        => ResolveAsync(options, new EnvironmentExpander(), cancellationToken);

    /// <summary>For tests: resolve with a custom <see cref="EnvironmentExpander"/>.</summary>
    internal static async Task<IReadOnlyList<ResolvedServer>> ResolveAsync(TargetOptions options, EnvironmentExpander expander, CancellationToken cancellationToken)
    {
        var resolver = new ResolverImpl(expander);
        var servers = await resolver.ResolveCoreAsync(options, cancellationToken);
        return resolver.ApplyAuthOverrides(servers, options.AuthOverrides);
    }

    private sealed class ResolverImpl
    {
        private readonly EnvironmentExpander _expander;
        private readonly AuthConfigParser _authParser;

        public ResolverImpl(EnvironmentExpander expander)
        {
            _expander = expander;
            _authParser = new AuthConfigParser(expander);
        }

        public async Task<IReadOnlyList<ResolvedServer>> ResolveCoreAsync(TargetOptions options, CancellationToken cancellationToken)
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

        private async Task<IReadOnlyList<ResolvedServer>> ResolveFromConfigAsync(TargetOptions options, CancellationToken cancellationToken)
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

        private List<ResolvedServer> ParseServers(JsonNode root, string baseDirectory)
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
                            servers.Add(ParseServerDefinition(item, GetExpandedString(item, "name", "servers[].name"), baseDirectory));
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
                        servers.Add(ParseServerDefinition(obj, GetExpandedString(obj, "name", "name") ?? "default", baseDirectory));
                    }
                    break;

                case JsonArray array:
                    foreach (var item in array.OfType<JsonObject>())
                    {
                        servers.Add(ParseServerDefinition(item, GetExpandedString(item, "name", "name") ?? $"server-{servers.Count + 1}", baseDirectory));
                    }
                    break;
            }

            return servers;
        }

        private static bool LooksLikeServerDefinition(JsonObject obj)
            => obj.ContainsKey("command") || obj.ContainsKey("url") || obj.ContainsKey("endpoint");

        private ResolvedServer ParseServerDefinition(JsonObject obj, string? nameHint, string baseDirectory)
        {
            var name = GetExpandedString(obj, "name", "name") ?? nameHint ?? throw new UserInputException("Each server definition needs a name.");
            var serverPath = $"servers.{name}";

            var command = GetExpandedString(obj, "command", $"{serverPath}.command");
            var urlText = GetExpandedString(obj, "url", $"{serverPath}.url") ?? GetExpandedString(obj, "endpoint", $"{serverPath}.endpoint");

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

                var headers = ParseObjectMap(obj["headers"] as JsonObject, $"{serverPath}.headers");
                var auth = _authParser.Parse(obj, name, headers);

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
                    Transport: ParseTransport(GetExpandedString(obj, "transport", $"{serverPath}.transport")),
                    Headers: headers,
                    Auth: auth);
            }

            var arguments = ParseStringArray(obj["args"] as JsonArray, $"{serverPath}.args");
            var workingDirectory = ResolvePath(baseDirectory, GetExpandedString(obj, "cwd", $"{serverPath}.cwd") ?? GetExpandedString(obj, "workingDirectory", $"{serverPath}.workingDirectory"));
            var environment = ParseObjectMap(obj["env"] as JsonObject, $"{serverPath}.env");

            // For stdio servers, an 'auth' block is parsed and validated here so misconfigurations
            // (typo'd 'authh', missing token, etc.) surface immediately. The stdio + auth combo is
            // rejected later in ApplyAuthOverrides unless --no-auth is set.
            var stdioAuth = _authParser.Parse(obj, name, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            return new ResolvedServer(
                Name: name,
                Kind: ConnectionKind.Stdio,
                Target: RenderCommandLine(command!, arguments),
                Source: "config",
                Command: command,
                CommandArguments: arguments,
                WorkingDirectory: workingDirectory,
                Environment: environment,
                Url: null,
                Transport: TransportPreference.Auto,
                Headers: new Dictionary<string, string>(),
                Auth: stdioAuth);
        }

        public IReadOnlyList<ResolvedServer> ApplyAuthOverrides(IReadOnlyList<ResolvedServer> servers, AuthOverrides overrides)
        {
            if (overrides is null || overrides.IsEmpty)
            {
                EnforceStdioAuthRule(servers, noAuth: false);
                return servers;
            }

            if (overrides.NoAuth)
            {
                // --no-auth wipes all auth on every server, both HTTP and stdio.
                return servers
                    .Select(server => server.Auth is null ? server : server with { Auth = null })
                    .ToArray();
            }

            var result = new ResolvedServer[servers.Count];
            for (var index = 0; index < servers.Count; index++)
            {
                var server = servers[index];

                if (server.Kind == ConnectionKind.Stdio)
                {
                    if (overrides.Kind.HasValue || overrides.Token is not null
                        || (overrides.Scopes is { Count: > 0 })
                        || overrides.RedirectUri is not null
                        || overrides.CacheName is not null)
                    {
                        // CLI overrides target HTTP servers. For stdio we silently skip the overlay
                        // (no token to apply) but still enforce the stdio + config-auth rule below.
                        result[index] = server;
                    }
                    else
                    {
                        result[index] = server;
                    }

                    continue;
                }

                var merged = MergeAuth(server.Auth, overrides, server.Name);
                result[index] = server with { Auth = merged };
            }

            EnforceStdioAuthRule(result, noAuth: false);
            return result;
        }

        private static void EnforceStdioAuthRule(IReadOnlyList<ResolvedServer> servers, bool noAuth)
        {
            if (noAuth)
            {
                return;
            }

            foreach (var server in servers)
            {
                if (server.Kind == ConnectionKind.Stdio && server.Auth is not null)
                {
                    throw new UserInputException(
                        $"Server '{server.Name}': authentication only applies to HTTP/SSE targets. " +
                        "Use '--no-auth' to suppress the configured 'auth' block when running this server.");
                }
            }
        }

        private static ResolvedAuth? MergeAuth(ResolvedAuth? configAuth, AuthOverrides overrides, string serverName)
        {
            // --auth replaces the config auth block entirely.
            if (overrides.Kind.HasValue)
            {
                return overrides.Kind.Value switch
                {
                    AuthKind.Bearer => new ResolvedAuth(
                        AuthKind.Bearer,
                        Token: overrides.Token
                            ?? throw new UserInputException(
                                $"Server '{serverName}': '--auth bearer' requires '--auth-token <value>'.")),
                    AuthKind.OAuth => new ResolvedAuth(
                        AuthKind.OAuth,
                        Scopes: overrides.Scopes,
                        RedirectUri: overrides.RedirectUri,
                        CacheName: overrides.CacheName),
                    AuthKind.None => null,
                    _ => throw new UserInputException(
                        $"Server '{serverName}': unsupported '--auth' value '{overrides.Kind.Value}'.")
                };
            }

            // No --auth: overlay individual fields onto config.
            if (configAuth is null)
            {
                if (overrides.Token is not null
                    || (overrides.Scopes is { Count: > 0 })
                    || overrides.RedirectUri is not null
                    || overrides.CacheName is not null)
                {
                    throw new UserInputException(
                        $"Server '{serverName}': auth field overrides (--auth-token, --scope, --redirect-uri, --token-cache-name) " +
                        "require either '--auth <type>' or an 'auth' block in the config.");
                }

                return null;
            }

            return configAuth with
            {
                Token = overrides.Token ?? configAuth.Token,
                Scopes = overrides.Scopes ?? configAuth.Scopes,
                RedirectUri = overrides.RedirectUri ?? configAuth.RedirectUri,
                CacheName = overrides.CacheName ?? configAuth.CacheName
            };
        }

        private TransportPreference ParseTransport(string? value) => value?.ToLowerInvariant() switch
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

        private string? GetExpandedString(JsonObject obj, string propertyName, string contextPath)
        {
            if (obj[propertyName] is not JsonValue value)
            {
                return null;
            }

            return _expander.Expand(value.GetValue<string>(), contextPath);
        }

        private IReadOnlyList<string> ParseStringArray(JsonArray? array, string contextPath)
        {
            if (array is null)
            {
                return [];
            }

            var values = new List<string>();
            for (var index = 0; index < array.Count; index++)
            {
                var item = array[index];
                if (item is null)
                {
                    continue;
                }

                values.Add(_expander.Expand(item.GetValue<string>(), $"{contextPath}[{index}]"));
            }

            return values;
        }

        private IReadOnlyDictionary<string, string> ParseObjectMap(JsonObject? obj, string contextPath)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (obj is null)
            {
                return values;
            }

            foreach (var entry in obj)
            {
                var entryPath = $"{contextPath}.{entry.Key}";
                values[entry.Key] = entry.Value switch
                {
                    null => string.Empty,
                    JsonValue value => _expander.Expand(value.GetValue<string>(), entryPath),
                    var node => node.ToJsonString()
                };
            }

            return values;
        }
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
}
