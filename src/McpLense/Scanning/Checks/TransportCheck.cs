using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Captures the unauthenticated probe result: HTTP status, security-relevant response
/// headers (HSTS / CORS / CSP / Server / X-Powered-By...), TLS leaf certificate, mixed
/// content flag. Wraps <see cref="TransportProbe"/>.
/// </summary>
internal sealed class TransportCheck : IScanCheck
{
    public string Id => "transport";
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Server.Kind != ConnectionKind.Http || context.Server.Url is null)
        {
            return CheckOutcome.Skipped;
        }

        using var probe = new TransportProbe();
        var result = await probe.ProbeAsync(context.Server.Url, cancellationToken).ConfigureAwait(false);

        var mixedContent = string.Equals(context.Server.Url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        var payload = new TransportData(
            MixedContent: mixedContent,
            StatusCode: result.StatusCode,
            Reached: result.Reached,
            Error: result.Error,
            Tls: result.Tls,
            ResponseHeaders: result.Headers);

        return new CheckOutcome(Ran: true, Data: ToNode(payload), Error: null);
    }

    private static JsonNode? ToNode(object value) => JsonSerializer.SerializeToNode(value, AuthCheck.SerializerOptions);

    internal sealed record TransportData(
        bool MixedContent,
        int? StatusCode,
        bool Reached,
        string? Error,
        TlsInfo? Tls,
        ResponseHeadersSummary? ResponseHeaders);
}
