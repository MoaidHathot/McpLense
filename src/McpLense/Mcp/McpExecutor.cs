using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace McpLense;

internal static class McpExecutor
{
    public static async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var servers = await TargetResolver.ResolveAsync(command.Target, cancellationToken);

        // Resolve auth profiles for HTTP servers that don't already have inline auth (i.e.
        // anything other than --auth bearer). --no-auth short-circuits this entirely so quick
        // local debugging works without any profile setup.
        if (!command.Target.AuthOverrides.NoAuth)
        {
            servers = await AttachProfilesAsync(servers, command.Target, cancellationToken).ConfigureAwait(false);
        }

        // --login and --logout short-circuit BEFORE the per-command dispatch so they reuse the
        // same target resolution + auth merging the underlying command would have used. The
        // selected AppCommand is intentionally ignored on this path; the user just needs a valid
        // target to identify which server(s) to (re-)authenticate.
        if (command.Target.AuthOverrides.LoginOnly)
        {
            var report = await DispatchLoginAsync(servers, cancellationToken);
            return new ExecutionOutcome(report, report.Servers.Any(entry => !entry.Success));
        }

        if (command.Target.AuthOverrides.LogoutOnly)
        {
            var report = await DispatchLogoutAsync(servers, cancellationToken);
            return new ExecutionOutcome(report, report.Servers.Any(entry => !entry.Success));
        }

        return command.Command switch
        {
            AppCommand.Inspect => await InspectAsync(servers, command.Timeout, cancellationToken),
            AppCommand.Tools => await ListToolsAsync(servers, command.Timeout, cancellationToken),
            AppCommand.Resources => await ListResourcesAsync(servers, command.Timeout, cancellationToken),
            AppCommand.Prompts => await ListPromptsAsync(servers, command.Timeout, cancellationToken),
            AppCommand.Call => await CallToolAsync(SingleServer(servers), command.Subject!, command.Arguments!, command.Timeout, command.ProgressEnabled, cancellationToken),
            AppCommand.Read => await ReadResourceAsync(SingleServer(servers), command.Subject!, command.Arguments, command.Timeout, cancellationToken),
            AppCommand.Prompt => await GetPromptAsync(SingleServer(servers), command.Subject!, command.Arguments!, command.Timeout, cancellationToken),
            _ => throw new UserInputException($"Unsupported command '{command.Command}'.")
        };
    }

    /// <summary>
    /// Attaches a resolved <see cref="AuthProfile"/>'s auth block onto every HTTP server that
    /// doesn't already have inline auth (e.g. set via <c>--auth bearer</c> in
    /// <see cref="TargetResolver.ApplyAuthOverrides"/>). Stdio servers are left alone.
    ///
    /// Profile attachment is conditional:
    /// <list type="bullet">
    ///   <item>If <c>--profile</c> is set explicitly → attach the named profile (error if missing).</item>
    ///   <item>If profiles are loaded (explicitly via <c>--profiles</c> OR auto-discovered from
    ///   the XDG default location) → probe the server URL; attach only when the probe says auth
    ///   is required (RFC 9728 metadata present, or the server emitted a <c>401</c>).</item>
    ///   <item>Otherwise → leave <c>Auth</c> at <c>null</c> (plain connection; the server will
    ///   surface a 401 if needed, which the user can resolve by adding a profile).</item>
    /// </list>
    /// Profiles come from the merged set of <c>--profiles</c> paths plus the XDG defaults
    /// (<see cref="DefaultConfigPaths"/>); duplicates across files raise a
    /// <see cref="UserInputException"/>.
    /// </summary>
    private static async Task<IReadOnlyList<ResolvedServer>> AttachProfilesAsync(
        IReadOnlyList<ResolvedServer> servers,
        TargetOptions target,
        CancellationToken cancellationToken)
    {
        var httpWithoutAuth = servers
            .Where(server => server.Kind == ConnectionKind.Http && server.Auth is null)
            .ToList();

        if (httpWithoutAuth.Count == 0)
        {
            return servers;
        }

        var explicitProfile = target.AuthOverrides.Profile;
        var profilePaths = ResolveProfilePaths(target.ProfilePaths);

        // No profiles, no --profile, no --try-all: skip the entire dance and let the runtime
        // attempt a plain connection. This preserves "just hit the URL" UX for servers that
        // don't need auth at all (the most common dev/test case).
        if (string.IsNullOrEmpty(explicitProfile) && !target.AuthOverrides.TryAll && profilePaths.Count == 0)
        {
            return servers;
        }

        var profiles = await ProfileLoader.LoadAsync(profilePaths, new EnvironmentExpander(), cancellationToken).ConfigureAwait(false);

        // --try-all is a runtime-only opt-in for now; runtime command paths still need a single
        // profile choice. Surface a clean error rather than silently picking one.
        if (target.AuthOverrides.TryAll)
        {
            throw new UserInputException(
                "--try-all is currently only supported with --login. Pick a profile with --profile <name> for runtime commands.");
        }

        using var probe = new AuthProbe();
        var resolver = new AuthProfileResolver(probe, new MsalCacheInspector());

        var result = new ResolvedServer[servers.Count];
        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];
            if (server.Kind != ConnectionKind.Http || server.Auth is not null)
            {
                result[index] = server;
                continue;
            }

            // When --profile was set explicitly, respect it unconditionally (no probe required).
            // Otherwise probe first; only attach a profile when the server signals auth is
            // required (status 401 or RFC 9728 metadata present).
            if (string.IsNullOrEmpty(explicitProfile))
            {
                var probeResult = await probe.ProbeAsync(server.Url!, cancellationToken).ConfigureAwait(false);
                if (probeResult.IsEmpty)
                {
                    // Server appears to need no auth (or it doesn't speak RFC 9728). Connect plain.
                    result[index] = server;
                    continue;
                }
            }

            var profile = await resolver.ResolveAsync(server.Url!, profiles, explicitProfile, cancellationToken).ConfigureAwait(false);
            result[index] = profile is null ? server : server with { Auth = profile.Auth };
        }

        return result;
    }

    /// <summary>
    /// Combines the user-supplied <c>--profiles</c> paths with the XDG default search results.
    /// Returns an empty list when nothing is found; the resolver surfaces a helpful error in
    /// that case.
    /// </summary>
    private static IReadOnlyList<string> ResolveProfilePaths(IReadOnlyList<string> explicitPaths)
    {
        if (explicitPaths.Count > 0)
        {
            return explicitPaths;
        }

        var root = DefaultConfigPaths.ResolveRoot();
        return DefaultConfigPaths.EnumerateProfileFiles(root);
    }

    private static ResolvedServer SingleServer(IReadOnlyList<ResolvedServer> servers)
        => servers.Count switch
        {
            1 => servers[0],
            0 => throw new UserInputException("No server was resolved."),
            _ => throw new UserInputException("This command requires exactly one server. Use --server with --config to select one.")
        };

    /// <summary>
    /// Routes each resolved server to the right login implementation based on its
    /// <see cref="AuthKind"/>. Servers without a recognised OAuth-family auth scheme surface as
    /// per-server failures via the shared <see cref="AuthSessionEntry"/> contract.
    /// </summary>
    private static async Task<AuthSessionReport> DispatchLoginAsync(IReadOnlyList<ResolvedServer> servers, CancellationToken cancellationToken)
    {
        var (oauth, interactive, unsupported) = PartitionByAuthKind(servers);

        var entries = new List<AuthSessionEntry>(servers.Count);
        if (oauth.Count > 0)
        {
            entries.AddRange((await AuthSessionRunner.LoginAsync(oauth, cancellationToken).ConfigureAwait(false)).Servers);
        }

        if (interactive.Count > 0)
        {
            entries.AddRange((await InteractiveBrowserSessionRunner.LoginAsync(interactive, cancellationToken).ConfigureAwait(false)).Servers);
        }

        entries.AddRange(unsupported.Select(server => new AuthSessionEntry(
            server.Name,
            server.Target,
            Success: false,
            Error: $"--login requires OAuth or interactive-browser authentication on '{server.Name}'.")));

        // Preserve the input ordering so the output report matches the user's --server order.
        return new AuthSessionReport(
            "login",
            DateTimeOffset.UtcNow,
            ReorderToInput(servers, entries));
    }

    private static async Task<AuthSessionReport> DispatchLogoutAsync(IReadOnlyList<ResolvedServer> servers, CancellationToken cancellationToken)
    {
        var (oauth, interactive, unsupported) = PartitionByAuthKind(servers);

        var entries = new List<AuthSessionEntry>(servers.Count);
        if (oauth.Count > 0)
        {
            entries.AddRange((await AuthSessionRunner.LogoutAsync(oauth, cancellationToken).ConfigureAwait(false)).Servers);
        }

        if (interactive.Count > 0)
        {
            entries.AddRange((await InteractiveBrowserSessionRunner.LogoutAsync(interactive, cancellationToken).ConfigureAwait(false)).Servers);
        }

        entries.AddRange(unsupported.Select(server => new AuthSessionEntry(
            server.Name,
            server.Target,
            Success: false,
            Error: $"--logout requires OAuth or interactive-browser authentication on '{server.Name}'.")));

        return new AuthSessionReport(
            "logout",
            DateTimeOffset.UtcNow,
            ReorderToInput(servers, entries));
    }

    private static (List<ResolvedServer> OAuth, List<ResolvedServer> Interactive, List<ResolvedServer> Unsupported) PartitionByAuthKind(IReadOnlyList<ResolvedServer> servers)
    {
        var oauth = new List<ResolvedServer>();
        var interactive = new List<ResolvedServer>();
        var unsupported = new List<ResolvedServer>();

        foreach (var server in servers)
        {
            switch (server.Auth?.Kind)
            {
                case AuthKind.OAuth:
                    oauth.Add(server);
                    break;
                case AuthKind.InteractiveBrowser:
                    interactive.Add(server);
                    break;
                default:
                    unsupported.Add(server);
                    break;
            }
        }

        return (oauth, interactive, unsupported);
    }

    private static IReadOnlyList<AuthSessionEntry> ReorderToInput(IReadOnlyList<ResolvedServer> servers, IReadOnlyList<AuthSessionEntry> entries)
    {
        var byName = entries.ToDictionary(static entry => entry.Name, StringComparer.Ordinal);
        var ordered = new List<AuthSessionEntry>(servers.Count);
        foreach (var server in servers)
        {
            if (byName.TryGetValue(server.Name, out var entry))
            {
                ordered.Add(entry);
            }
        }

        return ordered;
    }

    private static async Task<ExecutionOutcome> InspectAsync(IReadOnlyList<ResolvedServer> servers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(server => InspectServerAsync(server, timeout, cancellationToken)));
        var hasErrors = results.Any(HasInspectErrors);
        return new ExecutionOutcome(new InspectReport(DateTimeOffset.UtcNow, results), hasErrors);
    }

    private static async Task<ExecutionOutcome> ListToolsAsync(IReadOnlyList<ResolvedServer> servers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(server => WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var items = await LoadToolsAsync(client, ct);
            return new ServerItems<ToolInfo>(server.Name, FormatTransport(server.Kind), server.Target, items);
        })));

        return new ExecutionOutcome(new ToolListReport(DateTimeOffset.UtcNow, servers.Zip(results, ToServerItems).ToArray()), results.Any(result => result.Error is not null));
    }

    private static async Task<ExecutionOutcome> ListResourcesAsync(IReadOnlyList<ResolvedServer> servers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(server => WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var items = await LoadResourcesAsync(client, ct);
            return new ServerItems<ResourceInfo>(server.Name, FormatTransport(server.Kind), server.Target, items);
        })));

        return new ExecutionOutcome(new ResourceListReport(DateTimeOffset.UtcNow, servers.Zip(results, ToServerItems).ToArray()), results.Any(result => result.Error is not null));
    }

    private static async Task<ExecutionOutcome> ListPromptsAsync(IReadOnlyList<ResolvedServer> servers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(server => WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var items = await LoadPromptsAsync(client, ct);
            return new ServerItems<PromptInfo>(server.Name, FormatTransport(server.Kind), server.Target, items);
        })));

        return new ExecutionOutcome(new PromptListReport(DateTimeOffset.UtcNow, servers.Zip(results, ToServerItems).ToArray()), results.Any(result => result.Error is not null));
    }

    private static async Task<ExecutionOutcome> CallToolAsync(ResolvedServer server, string toolName, JsonObject arguments, TimeSpan timeout, bool progressEnabled, CancellationToken cancellationToken)
    {
        var progressUpdates = new List<ProgressUpdate>();
        var progress = progressEnabled ? new Progress<ProgressNotificationValue>(value =>
        {
            var update = new ProgressUpdate(value.Progress, value.Total, value.Message, DateTimeOffset.UtcNow);
            progressUpdates.Add(update);
            WriteProgress(server, toolName, update);
        }) : null;

        var result = await WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var response = await client.CallToolAsync(toolName, ToDictionary(arguments), progress: progress, options: null, cancellationToken: ct);
            return new ToolCallReport(DateTimeOffset.UtcNow, ToReference(server), toolName, arguments, progressUpdates.ToArray(), MapCallResult(response));
        });

        return new ExecutionOutcome(result.Value ?? new ToolCallReport(DateTimeOffset.UtcNow, ToReference(server), toolName, arguments, progressUpdates.ToArray(), null, result.Error), result.Error is not null || result.Value?.Result?.IsError == true);
    }

    private static async Task<ExecutionOutcome> ReadResourceAsync(ResolvedServer server, string resource, JsonObject? arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            object response = arguments is null
                ? await client.ReadResourceAsync(resource, options: null, cancellationToken: ct)
                : await client.ReadResourceAsync(resource, ToDictionary(arguments), options: null, cancellationToken: ct);

            return new ReadReport(DateTimeOffset.UtcNow, ToReference(server), resource, arguments, MapReadResult(response));
        });

        return new ExecutionOutcome(result.Value ?? new ReadReport(DateTimeOffset.UtcNow, ToReference(server), resource, arguments, null, result.Error), result.Error is not null);
    }

    private static async Task<ExecutionOutcome> GetPromptAsync(ResolvedServer server, string promptName, JsonObject arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var response = await client.GetPromptAsync(promptName, ToDictionary(arguments), options: null, cancellationToken: ct);
            return new PromptCallReport(DateTimeOffset.UtcNow, ToReference(server), promptName, arguments, MapPromptResult(response));
        });

        return new ExecutionOutcome(result.Value ?? new PromptCallReport(DateTimeOffset.UtcNow, ToReference(server), promptName, arguments, null, result.Error), result.Error is not null);
    }

    private static async Task<ServerInspection> InspectServerAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var capabilities = GetCapabilities(client);
            var tools = await TrySectionAsync(capabilities.Tools, () => LoadToolsAsync(client, ct));
            var resources = await TrySectionAsync(capabilities.Resources, () => LoadResourcesAsync(client, ct));
            var templates = await TrySectionAsync(capabilities.Resources, () => LoadResourceTemplatesAsync(client, ct));
            var prompts = await TrySectionAsync(capabilities.Prompts, () => LoadPromptsAsync(client, ct));

            return new ServerInspection(server.Name, FormatTransport(server.Kind), server.Target, capabilities, tools, resources, templates, prompts);
        });

        return result.Value ?? new ServerInspection(
            server.Name,
            FormatTransport(server.Kind),
            server.Target,
            new CapabilitySnapshot(false, false, false, false, false),
            new SectionResult<ToolInfo>(false, []),
            new SectionResult<ResourceInfo>(false, []),
            new SectionResult<ResourceTemplateInfo>(false, []),
            new SectionResult<PromptInfo>(false, []),
            result.Error);
    }

    private static bool HasInspectErrors(ServerInspection inspection)
        => inspection.Error is not null
           || inspection.Tools.Error is not null
           || inspection.Resources.Error is not null
           || inspection.ResourceTemplates.Error is not null
           || inspection.Prompts.Error is not null;

    private static async Task<SectionResult<T>> TrySectionAsync<T>(bool supported, Func<Task<IReadOnlyList<T>>> loader)
    {
        if (!supported)
        {
            return new SectionResult<T>(false, []);
        }

        try
        {
            return new SectionResult<T>(true, await loader());
        }
        catch (Exception ex)
        {
            return new SectionResult<T>(true, [], FormatException(ex));
        }
    }

    private static async Task<IReadOnlyList<ToolInfo>> LoadToolsAsync(McpClient client, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Cast<object>().Select(MapTool).ToArray();
    }

    private static async Task<IReadOnlyList<ResourceInfo>> LoadResourcesAsync(McpClient client, CancellationToken cancellationToken)
    {
        var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken);
        return resources.Cast<object>().Select(MapResource).ToArray();
    }

    private static async Task<IReadOnlyList<ResourceTemplateInfo>> LoadResourceTemplatesAsync(McpClient client, CancellationToken cancellationToken)
    {
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: cancellationToken);
        return templates.Cast<object>().Select(MapResourceTemplate).ToArray();
    }

    private static async Task<IReadOnlyList<PromptInfo>> LoadPromptsAsync(McpClient client, CancellationToken cancellationToken)
    {
        var prompts = await client.ListPromptsAsync(cancellationToken: cancellationToken);
        return prompts.Cast<object>().Select(MapPrompt).ToArray();
    }

    private static async Task<OperationResult<T>> WithClientAsync<T>(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken, Func<McpClient, CancellationToken, Task<T>> operation)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await using var client = await CreateClientAsync(server, timeout, timeoutSource.Token);
            return new OperationResult<T>(await operation(client, timeoutSource.Token), null);
        }
        catch (Exception ex)
        {
            return new OperationResult<T>(default, FormatException(ex));
        }
    }

    private static async Task<McpClient> CreateClientAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (server.Kind is ConnectionKind.Http)
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = server.Url!,
                Name = server.Name,
                TransportMode = ToHttpTransportMode(server.Transport),
                ConnectionTimeout = timeout
            };

            if (server.Headers.Count > 0)
            {
                SetProperty(options, server.Headers.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase), "AdditionalHeaders");
            }

            if (server.Auth is { Kind: not AuthKind.None })
            {
                var authHandler = AuthHandlerFactory.Create(server.Auth, server.Url);
                if (authHandler is not null)
                {
                    authHandler.InnerHandler = new SocketsHttpHandler();
                    var http = new HttpClient(authHandler, disposeHandler: true);
                    return await McpClient.CreateAsync(new HttpClientTransport(options, http, ownsHttpClient: true), cancellationToken: cancellationToken);
                }
            }

            return await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: cancellationToken);
        }

        var stdioOptions = new StdioClientTransportOptions
        {
            Command = server.Command!,
            Name = server.Name,
            Arguments = [.. server.CommandArguments],
            ShutdownTimeout = timeout
        };

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            stdioOptions.WorkingDirectory = server.WorkingDirectory;
        }

        if (server.Environment.Count > 0)
        {
            SetProperty(stdioOptions, server.Environment.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase), "EnvironmentVariables");
        }

        return await McpClient.CreateAsync(new StdioClientTransport(stdioOptions), cancellationToken: cancellationToken);
    }

    private static CapabilitySnapshot GetCapabilities(McpClient client)
    {
        var capabilities = GetPropertyValue(client, "ServerCapabilities");
        return new CapabilitySnapshot(
            Tools: GetPropertyValue(capabilities, "Tools") is not null,
            Resources: GetPropertyValue(capabilities, "Resources") is not null,
            Prompts: GetPropertyValue(capabilities, "Prompts") is not null,
            Logging: GetPropertyValue(capabilities, "Logging") is not null,
            Completions: GetPropertyValue(capabilities, "Completions") is not null);
    }

    private static ToolInfo MapTool(object tool)
        => new(
            Name: GetStringProperty(tool, "Name") ?? string.Empty,
            Description: GetStringProperty(tool, "Description"),
            InputSchema: ToJsonNode(GetPropertyValue(tool, "ProtocolTool", "InputSchema") ?? GetPropertyValue(tool, "InputSchema")));

    private static ResourceInfo MapResource(object resource)
        => new(
            Name: GetStringProperty(resource, "Name"),
            Uri: GetStringProperty(resource, "Uri"),
            MimeType: GetStringProperty(resource, "MimeType"),
            Description: GetStringProperty(resource, "Description"));

    private static ResourceTemplateInfo MapResourceTemplate(object template)
        => new(
            Name: GetStringProperty(template, "Name"),
            UriTemplate: GetStringProperty(template, "UriTemplate"),
            MimeType: GetStringProperty(template, "MimeType"),
            Description: GetStringProperty(template, "Description"));

    private static PromptInfo MapPrompt(object prompt)
    {
        var arguments = EnumerateObjects(GetPropertyValue(prompt, "ProtocolPrompt", "Arguments") ?? GetPropertyValue(prompt, "Arguments"))
            .Select(argument => new PromptArgumentInfo(
                Name: GetStringProperty(argument, "Name"),
                Description: GetStringProperty(argument, "Description"),
                Required: GetBoolProperty(argument, "Required") ?? false))
            .ToArray();

        return new PromptInfo(
            Name: GetStringProperty(prompt, "Name") ?? string.Empty,
            Description: GetStringProperty(prompt, "Description"),
            Arguments: arguments);
    }

    private static CallResultView MapCallResult(object result)
        => new(
            IsError: GetBoolProperty(result, "IsError"),
            StructuredContent: ToJsonNode(GetPropertyValue(result, "StructuredContent")),
            Meta: ToJsonNode(GetPropertyValue(result, "Meta") ?? GetPropertyValue(result, "_meta")),
            Content: EnumerateObjects(GetPropertyValue(result, "Content")).Select(MapContentBlock).ToArray());

    private static ReadResourceView MapReadResult(object result)
        => new(EnumerateObjects(GetPropertyValue(result, "Contents")).Select(MapResourceContent).ToArray());

    private static PromptResultView MapPromptResult(object result)
        => new(
            Description: GetStringProperty(result, "Description"),
            Messages: EnumerateObjects(GetPropertyValue(result, "Messages")).Select(MapPromptMessage).ToArray());

    private static PromptMessageView MapPromptMessage(object message)
        => new(
            Role: GetPropertyValue(message, "Role")?.ToString(),
            Content: GetPropertyValue(message, "Content") is { } content ? MapContentBlock(content) : null);

    private static ContentBlockView MapContentBlock(object block)
    {
        var typeName = block.GetType().Name;

        return typeName switch
        {
            "TextContentBlock" => new ContentBlockView(
                Kind: "text",
                Text: GetStringProperty(block, "Text"),
                MimeType: GetStringProperty(block, "MimeType")),
            "ImageContentBlock" => CreateBinaryContent("image", block),
            "AudioContentBlock" => CreateBinaryContent("audio", block),
            "EmbeddedResourceBlock" => new ContentBlockView(
                Kind: "resource",
                Resource: GetPropertyValue(block, "Resource") is { } resource ? MapResourceContent(resource) : null),
            _ => new ContentBlockView(typeName, Raw: ToJsonNode(block))
        };
    }

    private static ContentBlockView CreateBinaryContent(string kind, object block)
    {
        var bytes = TryGetBytes(GetPropertyValue(block, "DecodedData") ?? GetPropertyValue(block, "Data"));
        return new ContentBlockView(
            Kind: kind,
            MimeType: GetStringProperty(block, "MimeType"),
            DataBase64: bytes is null ? null : Convert.ToBase64String(bytes),
            ByteCount: bytes?.Length);
    }

    private static ResourceContentView MapResourceContent(object content)
    {
        var typeName = content.GetType().Name;
        return typeName switch
        {
            "TextResourceContents" => new ResourceContentView(
                Kind: "text",
                Uri: GetStringProperty(content, "Uri"),
                MimeType: GetStringProperty(content, "MimeType"),
                Text: GetStringProperty(content, "Text")),
            "BlobResourceContents" => CreateBlobResourceContent(content),
            _ => new ResourceContentView(typeName, Raw: ToJsonNode(content))
        };
    }

    private static ResourceContentView CreateBlobResourceContent(object content)
    {
        var bytes = TryGetBytes(GetPropertyValue(content, "Blob") ?? GetPropertyValue(content, "Data"));
        return new ResourceContentView(
            Kind: "blob",
            Uri: GetStringProperty(content, "Uri"),
            MimeType: GetStringProperty(content, "MimeType"),
            DataBase64: bytes is null ? null : Convert.ToBase64String(bytes),
            ByteCount: bytes?.Length);
    }

    private static ServerReference ToReference(ResolvedServer server)
        => new(server.Name, FormatTransport(server.Kind), server.Target);

    private static ServerItems<T> ToServerItems<T>(ResolvedServer server, OperationResult<ServerItems<T>> result)
        => result.Value ?? new ServerItems<T>(server.Name, FormatTransport(server.Kind), server.Target, [], result.Error);

    private static string FormatTransport(ConnectionKind kind)
        => kind is ConnectionKind.Http ? "http" : "stdio";

    private static HttpTransportMode ToHttpTransportMode(TransportPreference preference) => preference switch
    {
        TransportPreference.Auto => HttpTransportMode.AutoDetect,
        TransportPreference.StreamableHttp => HttpTransportMode.StreamableHttp,
        TransportPreference.Sse => HttpTransportMode.Sse,
        _ => HttpTransportMode.AutoDetect
    };

    private static string FormatException(Exception exception)
        => exception is OperationCanceledException ? "Timed out." : $"{exception.GetType().Name}: {exception.Message}";

    private static void WriteProgress(ResolvedServer server, string toolName, ProgressUpdate update)
    {
        var pieces = new List<string> { $"[{server.Name}] {toolName}" };

        if (update.Progress is not null && update.Total is not null)
        {
            pieces.Add($"{update.Progress:0.##}/{update.Total:0.##}");
        }
        else if (update.Progress is not null)
        {
            pieces.Add(update.Progress.Value <= 100 ? $"{update.Progress:0.##}%" : update.Progress.Value.ToString("0.##"));
        }

        if (!string.IsNullOrWhiteSpace(update.Message))
        {
            pieces.Add(update.Message!);
        }

        Console.Error.WriteLine(string.Join(" | ", pieces));
    }

    private static Dictionary<string, object?> ToDictionary(JsonObject obj)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in obj)
        {
            values[entry.Key] = ToClrValue(entry.Value);
        }

        return values;
    }

    private static object? ToClrValue(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject obj => obj.ToDictionary(static pair => pair.Key, static pair => ToClrValue(pair.Value), StringComparer.OrdinalIgnoreCase),
            JsonArray array => array.Select(ToClrValue).ToList(),
            JsonValue value when value.TryGetValue<bool>(out var boolValue) => boolValue,
            JsonValue value when value.TryGetValue<int>(out var intValue) => intValue,
            JsonValue value when value.TryGetValue<long>(out var longValue) => longValue,
            JsonValue value when value.TryGetValue<decimal>(out var decimalValue) => decimalValue,
            JsonValue value when value.TryGetValue<double>(out var doubleValue) => doubleValue,
            JsonValue value when value.TryGetValue<string>(out var stringValue) => stringValue,
            JsonValue value when value.TryGetValue<JsonElement>(out var element) => ToClrValue(element),
            _ => node.ToJsonString()
        };
    }

    private static object? ToClrValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(static property => property.Name, static property => ToClrValue(property.Value), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(ToClrValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };

    private static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            JsonNode node => node.DeepClone(),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            _ => JsonSerializer.SerializeToNode(value)
        };
    }

    private static string? GetStringProperty(object instance, string propertyName)
        => GetPropertyValue(instance, propertyName)?.ToString();

    private static bool? GetBoolProperty(object instance, string propertyName)
    {
        var value = GetPropertyValue(instance, propertyName);
        return value switch
        {
            bool boolValue => boolValue,
            null => null,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static object? GetPropertyValue(object? instance, params string[] propertyPath)
    {
        var current = instance;
        foreach (var propertyName in propertyPath)
        {
            if (current is null)
            {
                return null;
            }

            var property = current.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                return null;
            }

            current = property.GetValue(current);
        }

        return current;
    }

    private static IEnumerable<object> EnumerateObjects(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string)
        {
            yield return value;
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            yield break;
        }

        yield return value;
    }

    private static byte[]? TryGetBytes(object? value)
        => value switch
        {
            null => null,
            byte[] bytes => bytes,
            Memory<byte> memory => memory.ToArray(),
            ReadOnlyMemory<byte> readOnlyMemory => readOnlyMemory.ToArray(),
            ArraySegment<byte> segment => segment.ToArray(),
            IEnumerable<byte> enumerable => enumerable.ToArray(),
            _ => TryInvokeToArray(value)
        };

    private static byte[]? TryInvokeToArray(object value)
    {
        var method = value.GetType().GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance, []);
        return method?.Invoke(value, null) as byte[];
    }

    private static void SetProperty(object target, object? value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null || !property.CanWrite)
            {
                continue;
            }

            property.SetValue(target, ConvertValue(value, property.PropertyType));
            return;
        }
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
        {
            return null;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType.IsEnum && value is string enumText)
        {
            return Enum.Parse(targetType, enumText, true);
        }

        if (targetType == typeof(Uri) && value is string uriText)
        {
            return new Uri(uriText, UriKind.Absolute);
        }

        if (typeof(IDictionary<string, string>).IsAssignableFrom(targetType) && value is IEnumerable<KeyValuePair<string, string>> kvps)
        {
            return kvps.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        if (typeof(IEnumerable<string>).IsAssignableFrom(targetType) && value is IEnumerable<string> strings)
        {
            return strings.ToList();
        }

        return Convert.ChangeType(value, targetType);
    }

    private sealed record OperationResult<T>(T? Value, string? Error);
}
