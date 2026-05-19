using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpLense.Scanning.Checks.Behavior;

/// <summary>
/// Opens a dedicated MCP session with handlers wired for sampling / elicitation / roots
/// requests and notification listeners for the <c>notifications/*</c> family, then holds
/// the session open for the configured duration. Every inbound call the server initiates
/// is captured verbatim and surfaced in the report. Off by default; opt-in via config or
/// the <c>mcplense observe</c> command.
/// </summary>
internal sealed class ServerInitiatedObservationCheck : IScanCheck
{
    public string Id => "behavior.serverInitiated";

    // Run after the standard read checks. The observation session is a SEPARATE McpClient
    // because inbound-handler wiring has to happen at construction time; the shared session
    // was opened without these handlers by AuthCheck-driven first use.
    public IReadOnlyList<string> DependsOn => new[] { "tools", "prompts", "resources" };
    public bool IsEnabledByDefault => false;

    private static readonly string[] NotificationMethodsToWatch =
    {
        "notifications/tools/list_changed",
        "notifications/prompts/list_changed",
        "notifications/resources/list_changed",
        "notifications/resources/updated",
        "notifications/message",
        "notifications/progress"
    };

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Server.Kind != ConnectionKind.Http || context.Server.Url is null)
        {
            return CheckOutcome.Skipped;
        }

        var config = context.Config.GetCheckConfig(Id);
        var duration = TimeSpan.FromSeconds(
            (config?["observationDurationSeconds"] as JsonValue)?.GetValue<double>() ?? 2.0);
        var advertised = ParseAdvertised(config);

        var captured = new List<JsonNode>();
        var captureLock = new object();
        var clientOptions = BuildOptionsWithHandlers(advertised, captured, captureLock);

        McpClient? client = null;
        var sw = Stopwatch.StartNew();
        try
        {
            client = await OpenObservationSessionAsync(context, clientOptions, cancellationToken).ConfigureAwait(false);
            if (client is null)
            {
                var failure = new ObservationData(
                    ObservationDurationMs: 0,
                    AdvertisedCapabilities: advertised,
                    RefusalPolicy: "silent",
                    InboundRequests: new List<JsonNode>(),
                    InboundCountsByMethod: new Dictionary<string, int>(),
                    Error: context.SessionError ?? "Failed to open a dedicated observation session.");
                return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(failure), Error: null);
            }

            // Wire notification handlers AFTER init so the SDK's inbound dispatcher is
            // ready. Drain each IAsyncDisposable before disposing the client.
            var notificationHandles = new List<IAsyncDisposable>();
            foreach (var method in NotificationMethodsToWatch)
            {
                notificationHandles.Add(client.RegisterNotificationHandler(method, (notif, _) =>
                {
                    Record(captured, captureLock, "notification", method, notif.Params);
                    return ValueTask.CompletedTask;
                }));
            }

            try
            {
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            foreach (var handle in notificationHandles)
            {
                try { await handle.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            }
        }
        finally
        {
            sw.Stop();
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            }
        }

        List<JsonNode> snapshot;
        lock (captureLock)
        {
            snapshot = new List<JsonNode>(captured);
        }

        var byMethod = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in snapshot)
        {
            if (entry is JsonObject obj && obj["method"]?.GetValue<string>() is { } method)
            {
                byMethod[method] = byMethod.TryGetValue(method, out var n) ? n + 1 : 1;
            }
        }

        var data = new ObservationData(
            ObservationDurationMs: sw.Elapsed.TotalMilliseconds,
            AdvertisedCapabilities: advertised,
            RefusalPolicy: "silent",
            InboundRequests: snapshot,
            InboundCountsByMethod: byMethod);

        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(data), Error: null);
    }

    private static McpClientOptions BuildOptionsWithHandlers(
        IReadOnlyList<string> advertised,
        List<JsonNode> captured,
        object captureLock)
    {
        var advertiseSet = advertised.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var caps = new ClientCapabilities();
        if (advertiseSet.Contains("sampling"))
        {
            caps.Sampling = new SamplingCapability();
        }
        if (advertiseSet.Contains("elicitation"))
        {
            caps.Elicitation = new ElicitationCapability();
        }
        if (advertiseSet.Contains("roots"))
        {
            caps.Roots = new RootsCapability { ListChanged = advertiseSet.Contains("listChanged") };
        }

        var handlers = new McpClientHandlers
        {
            // Refuse every inbound request after capturing it. Throwing here makes the SDK
            // serialise a JSON-RPC error back to the server, which is the correct "we
            // can't help you" answer rather than simulating real LLM / user / FS work.
            SamplingHandler = (req, _, _) =>
            {
                Record(captured, captureLock, "request", "sampling/createMessage", req);
                throw new InvalidOperationException("Sampling refused by mcplense scan.");
            },
            ElicitationHandler = (req, _) =>
            {
                Record(captured, captureLock, "request", "elicitation/create", req);
                throw new InvalidOperationException("Elicitation refused by mcplense scan.");
            },
            RootsHandler = (req, _) =>
            {
                Record(captured, captureLock, "request", "roots/list", req);
                throw new InvalidOperationException("Roots query refused by mcplense scan.");
            }
        };

        return new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "mcplense", Version = "0.3" },
            Capabilities = caps,
            Handlers = handlers
        };
    }

    private static async Task<McpClient?> OpenObservationSessionAsync(
        ScanContext context,
        McpClientOptions clientOptions,
        CancellationToken cancellationToken)
    {
        if (context.Server.Url is null)
        {
            return null;
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = context.Server.Url,
            Name = context.Server.Name + "-observe",
            TransportMode = context.Server.Transport switch
            {
                TransportPreference.StreamableHttp => HttpTransportMode.StreamableHttp,
                TransportPreference.Sse => HttpTransportMode.Sse,
                _ => HttpTransportMode.AutoDetect
            },
            ConnectionTimeout = context.HandshakeTimeout
        };

        if (context.Server.Headers.Count > 0)
        {
            var prop = transportOptions.GetType().GetProperty("AdditionalHeaders");
            prop?.SetValue(transportOptions, context.Server.Headers.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        }

        var serverForSession = context.Server with { Auth = context.ActiveAuth };
        try
        {
            if (serverForSession.Auth is { Kind: not AuthKind.None })
            {
                var authHandler = AuthHandlerFactory.Create(serverForSession.Auth, serverForSession.Url);
                if (authHandler is not null)
                {
                    authHandler.InnerHandler = new SocketsHttpHandler();
                    var http = new HttpClient(authHandler, disposeHandler: true);
                    return await McpClient.CreateAsync(
                        new HttpClientTransport(transportOptions, http, ownsHttpClient: true),
                        clientOptions,
                        loggerFactory: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            return await McpClient.CreateAsync(
                new HttpClientTransport(transportOptions),
                clientOptions,
                loggerFactory: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static void Record(List<JsonNode> captured, object captureLock, string kind, string method, object? payload)
    {
        JsonNode? paramsNode = null;
        try
        {
            paramsNode = payload is null ? null : JsonSerializer.SerializeToNode(payload);
        }
        catch
        {
            paramsNode = JsonValue.Create(payload?.ToString());
        }

        var entry = new JsonObject
        {
            ["kind"] = kind,
            ["method"] = method,
            ["receivedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["params"] = paramsNode
        };

        lock (captureLock)
        {
            captured.Add(entry);
        }
    }

    private static IReadOnlyList<string> ParseAdvertised(JsonObject? config)
    {
        if (config?["advertiseCapabilities"] is not JsonArray arr)
        {
            return new[] { "sampling", "elicitation", "roots", "listChanged" };
        }

        return arr.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToArray();
    }

    internal sealed record ObservationData(
        double ObservationDurationMs,
        IReadOnlyList<string> AdvertisedCapabilities,
        string RefusalPolicy,
        IReadOnlyList<JsonNode> InboundRequests,
        IReadOnlyDictionary<string, int> InboundCountsByMethod,
        string? Error = null);
}
