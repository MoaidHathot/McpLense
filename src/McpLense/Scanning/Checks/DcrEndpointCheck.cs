using System.Text.Json.Nodes;
using McpLense.Scanning.TargetResolution;
using Microsoft.Extensions.DependencyInjection;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Off-by-default DCR (RFC 7591) endpoint surface probe. When the auth check classified
/// the server as oauth-rfc9728 AND the authorizationServers check fetched AS metadata
/// AND that metadata advertised a registration endpoint, do an unauthenticated OPTIONS +
/// empty POST and capture status / headers. Reveals open-DCR posture without writes.
/// </summary>
internal sealed class DcrEndpointCheck : IScanCheck
{
    public string Id => "dcrEndpoint";
    public IReadOnlyList<string> DependsOn => new[] { "authorizationServers" };
    public bool IsEnabledByDefault => false;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var asNode = context.GetPriorOutput("authorizationServers");
        if (asNode is not JsonObject asObj)
        {
            return CheckOutcome.Skipped;
        }

        var dcrEndpoint = (asObj["dcrFromResourceMetadata"] as JsonObject)?["endpoint"]?.GetValue<string>();
        if (string.IsNullOrEmpty(dcrEndpoint) || !Uri.TryCreate(dcrEndpoint, UriKind.Absolute, out var endpoint))
        {
            return CheckOutcome.Skipped;
        }

        // Prefer the shared HttpClient pool when the host wired AddMcpLense (registers the
        // "mcplense-probe" named client). Fall back to a one-off client when the pipeline
        // was built via ScanPipelineBuilder with a minimal ServiceCollection.
        var factory = context.Services.GetService<IHttpClientFactory>();
        HttpClient http;
        bool ownHttp;
        if (factory is not null)
        {
            http = factory.CreateClient(McpLenseServiceCollectionExtensions.ProbeHttpClientName);
            ownHttp = false;
        }
        else
        {
            http = new HttpClient(new SocketsHttpHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
            ownHttp = true;
        }
        try
        {

        // Per-target headers travel ONLY when the DCR endpoint is on the same origin as the
        // MCP server. The DCR endpoint usually lives on the AS host (e.g. login.micro-
        // soft.com) and per the cross-origin rule we must NOT leak MCP-server headers there.
        // For the rare case where DCR lives on the MCP host (some self-hosted setups), we
        // still honour scope=session as an explicit opt-out.
        IReadOnlyDictionary<string, string>? probeHeaders = null;
        if (context.Server.Url is not null
            && context.Server.HeaderScope == TargetScope.All
            && context.Server.Headers.Count > 0
            && IsSameOrigin(context.Server.Url, endpoint))
        {
            probeHeaders = context.Server.Headers;
        }

        var optionsResult = await TryAsync(http, HttpMethod.Options, endpoint, body: null, probeHeaders, cancellationToken).ConfigureAwait(false);
        var postResult = await TryAsync(http, HttpMethod.Post, endpoint, body: "{}", probeHeaders, cancellationToken).ConfigureAwait(false);

        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new DcrEndpointData(endpoint.ToString(), optionsResult, postResult)), Error: null);
        }
        finally
        {
            if (ownHttp)
            {
                http.Dispose();
            }
        }
    }

    private static async Task<EndpointProbe> TryAsync(
        HttpClient http,
        HttpMethod method,
        Uri endpoint,
        string? body,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, endpoint);
            if (additionalHeaders is { Count: > 0 })
            {
                foreach (var (name, value) in additionalHeaders)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            if (body is not null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var text = response.Content.Headers.ContentType is null
                ? null
                : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new EndpointProbe(
                Method: method.Method,
                StatusCode: (int)response.StatusCode,
                ContentType: response.Content.Headers.ContentType?.ToString(),
                BodyTruncated: text is { Length: > 2048 } ? text[..2048] + "...(truncated)" : text);
        }
        catch (Exception ex)
        {
            return new EndpointProbe(method.Method, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsSameOrigin(Uri a, Uri b)
        => string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
           && a.Port == b.Port;

    internal sealed record DcrEndpointData(string Endpoint, EndpointProbe Options, EndpointProbe Post);

    internal sealed record EndpointProbe(string Method, int? StatusCode, string? ContentType, string? BodyTruncated);
}
