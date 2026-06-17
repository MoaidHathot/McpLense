using System.Reflection;
using ModelContextProtocol.Client;

namespace McpLense;

/// <summary>
/// Outcome of attempting to open an MCP session and (optionally) enumerate top-level lists.
/// Capability counts are <c>null</c> when the corresponding capability is not advertised by
/// the server, OR when the enumeration call failed; success of the handshake itself is
/// reported via <see cref="Success"/>.
/// </summary>
internal sealed record HandshakeResult(
    bool Success,
    string? Error = null,
    int? ToolCount = null,
    int? ResourceCount = null,
    int? PromptCount = null);

/// <summary>
/// Abstracted MCP handshake attempt. Implemented in production by
/// <see cref="McpHandshakeProbe"/>; replaced with a stub in unit tests so we don't need real
/// HTTP and credential stacks to exercise <see cref="AuthScanner"/>.
/// </summary>
internal interface IMcpHandshakeProbe
{
    /// <summary>
    /// Try to open an MCP session against <paramref name="server"/> and read tool/resource/prompt
    /// list lengths. Always returns - all exceptions are captured into
    /// <see cref="HandshakeResult.Error"/>.
    /// </summary>
    Task<HandshakeResult> TryHandshakeAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IMcpHandshakeProbe"/> that opens a real MCP session via
/// <see cref="McpClient.CreateAsync"/>. Intentionally cheap: it only does the
/// <c>initialize</c> handshake plus three list calls (when the corresponding capability bit is
/// set), and gives up immediately on any error.
/// </summary>
internal sealed class McpHandshakeProbe : IMcpHandshakeProbe
{
    public async Task<HandshakeResult> TryHandshakeAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        McpClient? client = null;
        try
        {
            client = await CreateClientAsync(server, timeout, cts.Token).ConfigureAwait(false);

            var (toolCount, resourceCount, promptCount) = await CountAdvertisedListsAsync(client, cts.Token).ConfigureAwait(false);

            return new HandshakeResult(
                Success: true,
                ToolCount: toolCount,
                ResourceCount: resourceCount,
                PromptCount: promptCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HandshakeResult(Success: false, Error: FormatException(ex));
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Mirrors <see cref="McpExecutor.CreateClientAsync"/> but only handles the HTTP path. Stdio
    /// servers are never passed to <see cref="AuthScanner"/>'s HTTP scan path; the scanner
    /// short-circuits them as <c>stdio</c> before we get here.
    /// </summary>
    private static async Task<McpClient> CreateClientAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (server.Kind != ConnectionKind.Http || server.Url is null)
        {
            throw new InvalidOperationException("McpHandshakeProbe only supports HTTP MCP targets.");
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

        // Shared factory: standalone-stream suppression for parity with the runtime client (some
        // Streamable HTTP servers drop the session if the SDK's early standalone GET races ahead of
        // the async session commit -> -32001), auth chaining, and the streaming-safe timeout.
        var http = McpHttpClientFactory.Create(server, server.Auth);
        return await McpClient.CreateAsync(new HttpClientTransport(options, http, ownsHttpClient: true), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort enumeration of tool / resource / prompt counts, gated by the server's
    /// advertised capability bits. Each section is wrapped in its own try/catch so a single
    /// list call's failure (e.g. server crashed on <c>prompts/list</c>) doesn't poison the
    /// rest of the report.
    /// </summary>
    private static async Task<(int? Tools, int? Resources, int? Prompts)> CountAdvertisedListsAsync(McpClient client, CancellationToken cancellationToken)
    {
        var caps = GetPropertyValue(client, "ServerCapabilities");
        var supportsTools = GetPropertyValue(caps, "Tools") is not null;
        var supportsResources = GetPropertyValue(caps, "Resources") is not null;
        var supportsPrompts = GetPropertyValue(caps, "Prompts") is not null;

        int? toolCount = null;
        int? resourceCount = null;
        int? promptCount = null;

        if (supportsTools)
        {
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                toolCount = tools.Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Per-section failure is acceptable here: handshake succeeded, that's the
                // signal we care about. Leave toolCount null.
            }
        }

        if (supportsResources)
        {
            try
            {
                var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                resourceCount = resources.Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        if (supportsPrompts)
        {
            try
            {
                var prompts = await client.ListPromptsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                promptCount = prompts.Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return (toolCount, resourceCount, promptCount);
    }

    private static HttpTransportMode ToHttpTransportMode(TransportPreference preference) => preference switch
    {
        TransportPreference.Auto => HttpTransportMode.AutoDetect,
        TransportPreference.StreamableHttp => HttpTransportMode.StreamableHttp,
        TransportPreference.Sse => HttpTransportMode.Sse,
        _ => HttpTransportMode.AutoDetect
    };

    private static string FormatException(Exception exception)
        => exception is OperationCanceledException
            ? "Timed out."
            : $"{exception.GetType().Name}: {exception.Message}";

    // Mirror the small reflection helpers used by McpExecutor so we don't have to plumb them
    // through. The reflection surface is intentionally narrow - just enough to read advertised
    // capability bits and set the optional AdditionalHeaders property.

    private static object? GetPropertyValue(object? instance, string propertyName)
    {
        if (instance is null)
        {
            return null;
        }

        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return property?.GetValue(instance);
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
