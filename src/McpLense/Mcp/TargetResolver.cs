using System.Text.Json.Nodes;

namespace McpLense;

internal static class TargetResolver
{
    public static Task<IReadOnlyList<ResolvedServer>> ResolveAsync(TargetOptions options, CancellationToken cancellationToken)
        => ResolveAsync(options, new EnvironmentExpander(), cancellationToken);

    /// <summary>For tests: resolve with a custom <see cref="EnvironmentExpander"/>.</summary>
    internal static async Task<IReadOnlyList<ResolvedServer>> ResolveAsync(TargetOptions options, EnvironmentExpander expander, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(expander);

        var resolver = new ResolverImpl(expander);
        var servers = await resolver.ResolveCoreAsync(options, cancellationToken).ConfigureAwait(false);
        return resolver.ApplyAuthOverrides(servers, options.AuthOverrides);
    }

    private sealed class ResolverImpl
    {
        private readonly EnvironmentExpander _expander;

        public ResolverImpl(EnvironmentExpander expander)
        {
            _expander = expander;
        }

        public async Task<IReadOnlyList<ResolvedServer>> ResolveCoreAsync(TargetOptions options, CancellationToken cancellationToken)
        {
            if (options.ConfigPath is not null)
            {
                return await ResolveFromConfigAsync(options, cancellationToken).ConfigureAwait(false);
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

            var text = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new UserInputException($"Failed to parse config JSON: {ex.Message}");
            }

            if (root is null)
            {
                throw new UserInputException("Config file is empty.");
            }

            // Profile files and stdio configs are explicitly different artefacts. A user pointing
            // --config at a profile file probably meant --profiles; surface that loudly.
            if (root is JsonObject rootObj && rootObj.ContainsKey("authProfiles"))
            {
                throw new UserInputException(
                    $"Config file '{configPath}' contains an 'authProfiles' block. " +
                    "Pass profile files via --profiles instead of --config.");
            }

            var baseDirectory = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;
            var servers = ParseStdioServers(root, baseDirectory);

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

        /// <summary>
        /// Reads stdio MCP definitions from a config file. HTTP definitions and per-server
        /// 'auth' blocks are rejected with an actionable error so the user moves auth into
        /// profile files.
        /// </summary>
        private List<ResolvedServer> ParseStdioServers(JsonNode root, string baseDirectory)
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
                                servers.Add(ParseStdioDefinition(serverObject, entry.Key, baseDirectory));
                            }
                        }
                    }

                    if (obj["servers"] is JsonArray serverArray)
                    {
                        foreach (var item in serverArray.OfType<JsonObject>())
                        {
                            servers.Add(ParseStdioDefinition(item, GetExpandedString(item, "name", "servers[].name"), baseDirectory));
                        }
                    }

                    if (obj["servers"] is JsonObject serverObjectMap)
                    {
                        foreach (var entry in serverObjectMap)
                        {
                            if (entry.Value is JsonObject serverObject)
                            {
                                servers.Add(ParseStdioDefinition(serverObject, entry.Key, baseDirectory));
                            }
                        }
                    }

                    if (servers.Count == 0 && LooksLikeServerDefinition(obj))
                    {
                        servers.Add(ParseStdioDefinition(obj, GetExpandedString(obj, "name", "name") ?? "default", baseDirectory));
                    }
                    break;

                case JsonArray array:
                    foreach (var item in array.OfType<JsonObject>())
                    {
                        servers.Add(ParseStdioDefinition(item, GetExpandedString(item, "name", "name") ?? $"server-{servers.Count + 1}", baseDirectory));
                    }
                    break;
            }

            return servers;
        }

        private static bool LooksLikeServerDefinition(JsonObject obj)
            => obj.ContainsKey("command") || obj.ContainsKey("url") || obj.ContainsKey("endpoint");

        private ResolvedServer ParseStdioDefinition(JsonObject obj, string? nameHint, string baseDirectory)
        {
            var name = GetExpandedString(obj, "name", "name") ?? nameHint ?? throw new UserInputException("Each server definition needs a name.");
            var serverPath = $"servers.{name}";

            var command = GetExpandedString(obj, "command", $"{serverPath}.command");
            var urlText = GetExpandedString(obj, "url", $"{serverPath}.url") ?? GetExpandedString(obj, "endpoint", $"{serverPath}.endpoint");

            // Phase A breaking change: HTTP servers and per-server auth blocks no longer live in
            // --config files. They moved to profile files (auth) + positional URLs (target).
            if (!string.IsNullOrWhiteSpace(urlText))
            {
                throw new UserInputException(
                    $"Server '{name}' defines a URL. HTTP MCP servers must be passed positionally " +
                    "(e.g. 'mcplense inspect <url> --profile <name>'). Config files are stdio-only.");
            }

            if (obj.ContainsKey("auth"))
            {
                throw new UserInputException(
                    $"Server '{name}' has an 'auth' block. Per-server auth is no longer supported; " +
                    "move authentication into a profile file and pass it via --profiles.");
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                throw new UserInputException($"Server '{name}' must define a 'command' (config files are stdio-only).");
            }

            var arguments = ParseStringArray(obj["args"] as JsonArray, $"{serverPath}.args");
            var workingDirectory = ResolvePath(baseDirectory, GetExpandedString(obj, "cwd", $"{serverPath}.cwd") ?? GetExpandedString(obj, "workingDirectory", $"{serverPath}.workingDirectory"));
            var environment = ParseObjectMap(obj["env"] as JsonObject, $"{serverPath}.env");

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
                Auth: null);
        }

        public IReadOnlyList<ResolvedServer> ApplyAuthOverrides(IReadOnlyList<ResolvedServer> servers, AuthOverrides overrides)
        {
            if (overrides is null || overrides.IsEmpty)
            {
                return servers;
            }

            if (overrides.NoAuth)
            {
                // --no-auth wipes all auth on every server, both HTTP and stdio.
                return servers
                    .Select(server => server.Auth is null ? server : server with { Auth = null })
                    .ToArray();
            }

            // Ad-hoc Bearer is the only direct-CLI auth path remaining: every other scheme MUST
            // come from a profile (resolved by McpExecutor before opening transports).
            if (overrides.Kind == AuthKind.Bearer)
            {
                if (string.IsNullOrEmpty(overrides.Token))
                {
                    throw new UserInputException("'--auth bearer' requires '--auth-token <value>'.");
                }

                var bearerAuth = new ResolvedAuth(AuthKind.Bearer, Token: overrides.Token);
                return servers
                    .Select(server => server.Kind == ConnectionKind.Http ? server with { Auth = bearerAuth } : server)
                    .ToArray();
            }

            if (overrides.Kind.HasValue && overrides.Kind != AuthKind.Bearer)
            {
                throw new UserInputException(
                    $"--auth {overrides.Kind.Value.ToString().ToLowerInvariant()} is no longer accepted ad-hoc; " +
                    "create an authProfiles entry and pass --profile <name> instead.");
            }

            // No --auth, but a Token without --auth bearer still fails fast.
            if (overrides.Token is not null)
            {
                throw new UserInputException("--auth-token requires '--auth bearer'.");
            }

            return servers;
        }

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
