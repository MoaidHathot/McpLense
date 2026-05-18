using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpLense;

/// <summary>
/// Result of opening an MCP session against a server and running every read-only
/// enumeration the audit needs in one go: server info / protocol details / tools / prompts /
/// resources / resource templates / a non-existent-tool probe.
/// </summary>
internal sealed record InspectionOutcome(
    bool Success,
    string? FetchedVia,
    string? Error,
    ServerInfoSummary? ServerInfo,
    ProtocolSummary? Protocol,
    IReadOnlyList<ToolEntry> Tools,
    IReadOnlyList<PromptEntry> Prompts,
    IReadOnlyList<ResourceEntry> Resources,
    IReadOnlyList<ResourceTemplateEntry> Templates,
    CallNonExistentToolProbe? CallNonExistentTool);

/// <summary>
/// Abstracted "open an MCP session and read everything the audit cares about". Implemented in
/// production by <see cref="McpSessionInspector"/>; tests substitute a stub so the auditor's
/// orchestration is exercised without real HTTP / transport stacks.
/// </summary>
internal interface IMcpSessionInspector
{
    Task<InspectionOutcome> InspectAsync(
        ResolvedServer server,
        string fetchedVia,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class McpSessionInspector : IMcpSessionInspector
{
    // A name no real server would expose. We deliberately include "mcplense" so audit logs
    // attributing tool calls back to us are unambiguous.
    private const string NonExistentToolName = "__mcplense_audit_probe_nonexistent_tool__";

    public async Task<InspectionOutcome> InspectAsync(
        ResolvedServer server,
        string fetchedVia,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        McpClient? client = null;
        try
        {
            client = await CreateClientAsync(server, timeout, cts.Token).ConfigureAwait(false);

            var serverInfo = MapServerInfo(client);
            var protocol = MapProtocol(client);

            var tools = await SafeListAsync(
                () => client.ListToolsAsync(cancellationToken: cts.Token).AsTask(),
                MapTool).ConfigureAwait(false);
            var prompts = await SafeListAsync(
                () => client.ListPromptsAsync(cancellationToken: cts.Token).AsTask(),
                MapPrompt).ConfigureAwait(false);
            var resources = await SafeListAsync(
                () => client.ListResourcesAsync(cancellationToken: cts.Token).AsTask(),
                MapResource).ConfigureAwait(false);
            var templates = await SafeListAsync(
                () => client.ListResourceTemplatesAsync(cancellationToken: cts.Token).AsTask(),
                MapResourceTemplate).ConfigureAwait(false);

            var nonExistent = await ProbeNonExistentToolAsync(client, fetchedVia, cts.Token).ConfigureAwait(false);

            return new InspectionOutcome(
                Success: true,
                FetchedVia: fetchedVia,
                Error: null,
                ServerInfo: serverInfo,
                Protocol: protocol,
                Tools: tools,
                Prompts: prompts,
                Resources: resources,
                Templates: templates,
                CallNonExistentTool: nonExistent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InspectionOutcome(
                Success: false,
                FetchedVia: null,
                Error: $"{ex.GetType().Name}: {ex.Message}",
                ServerInfo: null,
                Protocol: null,
                Tools: [],
                Prompts: [],
                Resources: [],
                Templates: [],
                CallNonExistentTool: null);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<McpClient> CreateClientAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Mirror McpExecutor.CreateClientAsync's HTTP path. The audit currently runs against
        // HTTP MCPs only - stdio enumeration would require launching subprocesses which is
        // out of scope for v1 (the user explicitly excluded stdio supply-chain probing).
        if (server.Kind != ConnectionKind.Http || server.Url is null)
        {
            throw new InvalidOperationException("McpSessionInspector only supports HTTP MCP targets.");
        }

        var options = new HttpClientTransportOptions
        {
            Endpoint = server.Url,
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
                return await McpClient.CreateAsync(new HttpClientTransport(options, http, ownsHttpClient: true), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static ServerInfoSummary? MapServerInfo(McpClient client)
    {
        // McpClient exposes ServerInfo as Implementation; we mirror the fields verbatim.
        // _meta is exposed only when the server set it - we surface it as a JsonNode tree so
        // consumers can pattern-match against vendor extensions without us guessing the shape.
        var serverInfo = client.ServerInfo;
        if (serverInfo is null)
        {
            return null;
        }

        return new ServerInfoSummary(
            Name: serverInfo.Name,
            Title: serverInfo.Title,
            Version: serverInfo.Version,
            Description: serverInfo.Description,
            WebsiteUrl: serverInfo.WebsiteUrl,
            Meta: SafeMeta(GetPropertyValue(serverInfo, "Meta")));
    }

    private static ProtocolSummary MapProtocol(McpClient client)
    {
        var capabilities = client.ServerCapabilities;
        var instructions = client.ServerInstructions;
        var negotiated = client.NegotiatedProtocolVersion;

        return new ProtocolSummary(
            NegotiatedProtocolVersion: negotiated,
            Capabilities: MapCapabilities(capabilities),
            Instructions: instructions,
            InstructionsLength: instructions?.Length,
            Meta: null);  // top-level _meta currently isn't exposed by McpClient; consumers can read it via Protocol.Meta on subobjects.
    }

    private static CapabilitiesView MapCapabilities(ServerCapabilities? caps)
    {
        if (caps is null)
        {
            return new CapabilitiesView(null, null, null, null, null, null, null, null);
        }

        // Each sub-capability is null when the server didn't advertise it, populated otherwise.
        // This preserves the "declared vs not-declared" distinction the consumer needs.
        ToolsCapabilityView? tools = caps.Tools is null
            ? null
            : new ToolsCapabilityView(ListChanged: GetBoolProperty(caps.Tools, "ListChanged"));
        PromptsCapabilityView? prompts = caps.Prompts is null
            ? null
            : new PromptsCapabilityView(ListChanged: GetBoolProperty(caps.Prompts, "ListChanged"));
        ResourcesCapabilityView? resources = caps.Resources is null
            ? null
            : new ResourcesCapabilityView(
                ListChanged: GetBoolProperty(caps.Resources, "ListChanged"),
                Subscribe: GetBoolProperty(caps.Resources, "Subscribe"));

        CapabilityFlagView? logging = caps.Logging is null ? null : new CapabilityFlagView();
        CapabilityFlagView? completions = caps.Completions is null ? null : new CapabilityFlagView();
        CapabilityFlagView? tasks = GetPropertyValue(caps, "Tasks") is null ? null : new CapabilityFlagView();

        var experimental = SafeMeta(caps.Experimental);
        var extensions = SafeMeta(GetPropertyValue(caps, "Extensions"));

        return new CapabilitiesView(tools, prompts, resources, logging, completions, tasks, experimental, extensions);
    }

    private static async Task<IReadOnlyList<TOut>> SafeListAsync<TItem, TOut>(
        Func<Task<IList<TItem>>> fetch,
        Func<TItem, TOut> map)
    {
        try
        {
            var items = await fetch().ConfigureAwait(false);
            var mapped = new List<TOut>(items.Count);
            foreach (var item in items)
            {
                mapped.Add(map(item));
            }

            return mapped;
        }
        catch
        {
            // Per-section failure is acceptable: the rest of the audit should still surface.
            // We deliberately swallow because the InspectionOutcome doesn't carry per-section
            // errors right now; consumers can look at server capability bits to spot
            // unsupported sections, and at FetchError on the listing for global failures.
            return [];
        }
    }

    private static ToolEntry MapTool(McpClientTool tool)
    {
        var protocolTool = tool.ProtocolTool;
        var annotations = protocolTool?.Annotations;
        var missing = ComputeMissingAnnotations(annotations);

        return new ToolEntry(
            Name: tool.Name,
            Title: protocolTool?.Title ?? tool.Title,
            Description: tool.Description,
            InputSchema: ToJsonNode(protocolTool?.InputSchema ?? tool.JsonSchema),
            OutputSchema: ToJsonNode(protocolTool?.OutputSchema ?? tool.ReturnJsonSchema),
            Annotations: annotations is null
                ? null
                : new ToolAnnotationsView(
                    Title: annotations.Title,
                    ReadOnlyHint: annotations.ReadOnlyHint,
                    DestructiveHint: annotations.DestructiveHint,
                    IdempotentHint: annotations.IdempotentHint,
                    OpenWorldHint: annotations.OpenWorldHint),
            MissingAnnotations: missing,
            Meta: SafeMeta(GetPropertyValue(protocolTool, "Meta")));
    }

    /// <summary>
    /// Lists which of MCP's documented annotation hints the server did NOT supply. Per the
    /// user's request, the audit reports missing annotations as a factual list rather than
    /// labelling them as warnings - consumers decide their own policy ("tools without
    /// destructiveHint are blocked", "tools without readOnlyHint require explicit approval",
    /// etc.).
    /// </summary>
    private static IReadOnlyList<string> ComputeMissingAnnotations(ToolAnnotations? annotations)
    {
        // Per MCP spec the standard annotation hints are these four booleans. `Title` is
        // separate (it's a string label, not a behavioural hint), so we don't include it in
        // the missing list.
        var hintNames = new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" };

        if (annotations is null)
        {
            return hintNames;
        }

        var missing = new List<string>(hintNames.Length);
        if (annotations.ReadOnlyHint is null) missing.Add("readOnlyHint");
        if (annotations.DestructiveHint is null) missing.Add("destructiveHint");
        if (annotations.IdempotentHint is null) missing.Add("idempotentHint");
        if (annotations.OpenWorldHint is null) missing.Add("openWorldHint");
        return missing;
    }

    private static PromptEntry MapPrompt(McpClientPrompt prompt)
    {
        var protocolPrompt = prompt.ProtocolPrompt;
        var arguments = (protocolPrompt?.Arguments ?? [])
            .Select(arg => new PromptArgumentInfo(
                Name: arg.Name,
                Description: arg.Description,
                Required: arg.Required ?? false))
            .ToArray();

        return new PromptEntry(
            Name: prompt.Name,
            Title: protocolPrompt?.Title,
            Description: prompt.Description,
            Arguments: arguments,
            Meta: SafeMeta(GetPropertyValue(protocolPrompt, "Meta")));
    }

    private static ResourceEntry MapResource(McpClientResource resource)
    {
        var protocolResource = resource.ProtocolResource;
        var uri = protocolResource?.Uri ?? resource.Uri;
        var scheme = TryGetScheme(uri);

        return new ResourceEntry(
            Name: resource.Name,
            Title: protocolResource?.Title,
            Uri: uri,
            UriScheme: scheme,
            MimeType: protocolResource?.MimeType ?? resource.MimeType,
            Size: protocolResource?.Size,
            Description: resource.Description,
            Meta: SafeMeta(GetPropertyValue(protocolResource, "Meta")));
    }

    private static ResourceTemplateEntry MapResourceTemplate(McpClientResourceTemplate template)
    {
        var protocolTemplate = template.ProtocolResourceTemplate;
        return new ResourceTemplateEntry(
            Name: template.Name,
            Title: protocolTemplate?.Title,
            UriTemplate: protocolTemplate?.UriTemplate ?? template.UriTemplate,
            MimeType: protocolTemplate?.MimeType ?? template.MimeType,
            Description: template.Description,
            Meta: SafeMeta(GetPropertyValue(protocolTemplate, "Meta")));
    }

    private static string? TryGetScheme(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return parsed.Scheme;
        }

        // For non-URI strings (custom resource handles like "memory://something" that don't
        // parse as absolute URIs on all platforms), do a cheap prefix scan.
        var colon = uri.IndexOf(':');
        return colon > 0 ? uri[..colon] : null;
    }

    private static async Task<CallNonExistentToolProbe?> ProbeNonExistentToolAsync(McpClient client, string fetchedVia, CancellationToken cancellationToken)
    {
        // Call a tool name the server (presumably) doesn't expose. We capture whatever the
        // server hands back: well-behaved servers respond with a standard JSON-RPC method-not-
        // found error; some implementations return a tool result with isError=true; others
        // leak stack traces, internal hostnames, the full tool registry, etc. The audit just
        // records the response in one of three structurally-distinct shapes (see
        // CallNonExistentToolProbe.Outcome) - consumers judge.
        try
        {
            var response = await client.CallToolAsync(NonExistentToolName, options: null, cancellationToken: cancellationToken).ConfigureAwait(false);

            // CallToolAsync returned a tool result envelope. ToolResultIsError reflects the
            // server's own isError flag; ToolResultJson is the verbatim serialised envelope
            // so consumers can read whatever else the server included (content blocks,
            // structured content, _meta).
            return new CallNonExistentToolProbe(
                Attempted: true,
                ToolNameUsed: NonExistentToolName,
                FetchedVia: fetchedVia,
                Outcome: CallNonExistentToolOutcomes.ToolResultReturned,
                ToolResultIsError: GetBoolProperty(response, "IsError"),
                ToolResultJson: SerializeResponse(response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // McpProtocolException carries a JSON-RPC error code via its ErrorCode property
            // (we read it through reflection so we don't pin the exception type - the SDK has
            // had multiple names for this hierarchy). Bare McpException and anything else
            // (HttpRequestException etc.) are transport / framework failures.
            var typeName = ex.GetType().Name;
            var isProtocolError = typeName.Contains("McpProtocol", StringComparison.Ordinal)
                                  || typeName.Equals("McpException", StringComparison.Ordinal);

            if (isProtocolError)
            {
                int? errorCode = null;
                var raw = GetPropertyValue(ex, "ErrorCode");
                if (raw is int i)
                {
                    errorCode = i;
                }
                else if (raw is not null && int.TryParse(raw.ToString(), out var parsed))
                {
                    errorCode = parsed;
                }
                else if (raw is not null && raw.GetType().IsEnum)
                {
                    errorCode = (int)Convert.ChangeType(raw, typeof(int));
                }

                return new CallNonExistentToolProbe(
                    Attempted: true,
                    ToolNameUsed: NonExistentToolName,
                    FetchedVia: fetchedVia,
                    Outcome: CallNonExistentToolOutcomes.JsonRpcError,
                    JsonRpcErrorCode: errorCode,
                    JsonRpcErrorMessage: ex.Message);
            }

            return new CallNonExistentToolProbe(
                Attempted: true,
                ToolNameUsed: NonExistentToolName,
                FetchedVia: fetchedVia,
                Outcome: CallNonExistentToolOutcomes.TransportError,
                TransportError: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? SerializeResponse(object response)
    {
        try
        {
            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return response?.ToString();
        }
    }

    private static JsonNode? SafeMeta(object? meta)
    {
        if (meta is null)
        {
            return null;
        }

        try
        {
            return meta switch
            {
                JsonNode node => node.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                _ => JsonSerializer.SerializeToNode(meta)
            };
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return value switch
            {
                JsonNode node => node.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                _ => JsonSerializer.SerializeToNode(value)
            };
        }
        catch
        {
            return null;
        }
    }

    private static HttpTransportMode ToHttpTransportMode(TransportPreference preference) => preference switch
    {
        TransportPreference.Auto => HttpTransportMode.AutoDetect,
        TransportPreference.StreamableHttp => HttpTransportMode.StreamableHttp,
        TransportPreference.Sse => HttpTransportMode.Sse,
        _ => HttpTransportMode.AutoDetect
    };

    // Reflection helpers mirroring McpExecutor's, kept local so the inspector can read fields
    // that the SDK may rename without us blowing up - the audit is a "best-effort report" and
    // each missing field just shows up as null in the output.

    private static object? GetPropertyValue(object? instance, string propertyName)
    {
        if (instance is null)
        {
            return null;
        }

        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return property?.GetValue(instance);
    }

    private static bool? GetBoolProperty(object? instance, string propertyName)
    {
        var value = GetPropertyValue(instance, propertyName);
        return value switch
        {
            bool b => b,
            null => null,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static void SetProperty(object target, object? value, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        property.SetValue(target, value);
    }
}
