using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

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

        // Prefer the DI-registered HttpClient factory ("mcplense-probe") so socket pooling
        // and timeouts stay centralised. Falls back to the parameterless constructor when
        // the host wired the pipeline without DI.
        var factory = context.Services.GetService<IHttpClientFactory>();
        using var probe = factory is null ? new TransportProbe() : new TransportProbe(factory);
        // Apply per-target headers (e.g. x-mcp-ec-organization) to the unauthenticated probe
        // when the target overlay declares scope=all (the default). Scope=session keeps the
        // probe bare so the user can observe how an UNauthenticated request to the server
        // behaves regardless of per-target header config.
        var result = await probe.ProbeAsync(
            context.Server.Url,
            context.Server.Headers.Count == 0 ? null : context.Server.Headers,
            context.Server.HeaderScope,
            cancellationToken).ConfigureAwait(false);

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
