using System.Text.Json.Nodes;
using McpLense.Scanning.TargetResolution;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Re-probes the target with the active session's auth, capturing the response headers
/// from an authenticated request (which sometimes differ from the anonymous probe's
/// headers - some servers add HSTS / different CORS only after auth). Skipped when no
/// auth path is available.
/// </summary>
internal sealed class AuthenticatedHeadersCheck : IScanCheck
{
    public string Id => "authenticatedHeaders";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Server.Kind != ConnectionKind.Http || context.Server.Url is null)
        {
            return CheckOutcome.Skipped;
        }

        if (context.ActiveAuth is null)
        {
            // Anonymous - the transport check already captured this. Nothing distinct to add.
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new HeadersData(false, "Anonymous: see transport.responseHeaders", null)), Error: null);
        }

        try
        {
            var handler = AuthHandlerFactory.Create(context.ActiveAuth, context.Server.Url);
            if (handler is null)
            {
                return CheckOutcome.Skipped;
            }

            handler.InnerHandler = new SocketsHttpHandler();
            using var http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = context.HandshakeTimeout
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, context.Server.Url);

            // Per-target headers (scope=all) ride along with the authenticated probe so
            // server-side header-gated behaviour can be observed end-to-end. Scope=session
            // suppresses this on the probe.
            if (context.Server.HeaderScope == TargetScope.All && context.Server.Headers.Count > 0)
            {
                foreach (var (name, value) in context.Server.Headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            string? Header(string name)
                => response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

            var summary = new ResponseHeadersSummary(
                Server: Header("Server"),
                XPoweredBy: Header("X-Powered-By"),
                StrictTransportSecurity: Header("Strict-Transport-Security"),
                ContentSecurityPolicy: Header("Content-Security-Policy"),
                XFrameOptions: Header("X-Frame-Options"),
                XContentTypeOptions: Header("X-Content-Type-Options"),
                ReferrerPolicy: Header("Referrer-Policy"),
                AccessControlAllowOrigin: Header("Access-Control-Allow-Origin"),
                AccessControlAllowCredentials: Header("Access-Control-Allow-Credentials"),
                CacheControl: Header("Cache-Control"),
                Other: new Dictionary<string, string>());

            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new HeadersData(true, null, summary)), Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal sealed record HeadersData(bool Fetched, string? Detail, ResponseHeadersSummary? Headers);
}
