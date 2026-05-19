using System.Text.Json.Nodes;

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

        using var http = new HttpClient(new SocketsHttpHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var optionsResult = await TryAsync(http, HttpMethod.Options, endpoint, body: null, cancellationToken).ConfigureAwait(false);
        var postResult = await TryAsync(http, HttpMethod.Post, endpoint, body: "{}", cancellationToken).ConfigureAwait(false);

        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new DcrEndpointData(endpoint.ToString(), optionsResult, postResult)), Error: null);
    }

    private static async Task<EndpointProbe> TryAsync(HttpClient http, HttpMethod method, Uri endpoint, string? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, endpoint);
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

    internal sealed record DcrEndpointData(string Endpoint, EndpointProbe Options, EndpointProbe Post);

    internal sealed record EndpointProbe(string Method, int? StatusCode, string? ContentType, string? BodyTruncated);
}
