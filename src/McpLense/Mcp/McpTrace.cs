using System.Diagnostics;
using System.Text;
using McpLense.Diagnostics;

namespace McpLense;

/// <summary>
/// Process-wide toggle for wire tracing. Set by the CLI's <c>--trace</c> flag before any connection
/// is opened; read by <see cref="McpHttpClientFactory"/> to insert a <see cref="TraceLoggingHandler"/>.
/// A single-command-per-process CLI doesn't need anything fancier than a static flag.
/// </summary>
internal static class McpTrace
{
    public static bool Enabled { get; set; }
}

/// <summary>
/// Logs each HTTP MCP request/response (method, URL, JSON-RPC body, status, content-type, elapsed) to
/// the diagnostic sink when <see cref="McpTrace.Enabled"/>. The outgoing request body is buffered
/// first so logging never consumes the content the transport then sends; the response body is only
/// read for <c>application/json</c> (and re-wrapped) so SSE/streaming responses are left untouched.
/// </summary>
internal sealed class TraceLoggingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaxBody = 800;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!McpTrace.Enabled)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        McpLenseLog.Write($"trace --> {request.Method} {request.RequestUri}");
        if (request.Content is not null)
        {
            try
            {
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    McpLenseLog.Write($"trace     req: {Truncate(body)}");
                }
            }
            catch
            {
                // best-effort tracing; never let it break the actual call
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var contentType = response.Content.Headers.ContentType?.MediaType;
        McpLenseLog.Write($"trace <-- {(int)response.StatusCode} {response.ReasonPhrase} ({contentType}) {stopwatch.ElapsedMilliseconds}ms");

        if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    McpLenseLog.Write($"trace     res: {Truncate(body)}");
                }

                var rewrapped = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                foreach (var header in response.Content.Headers)
                {
                    rewrapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                response.Content = rewrapped;
            }
            catch
            {
                // best-effort; the original response is still returned
            }
        }

        return response;
    }

    private static string Truncate(string text) => text.Length <= MaxBody ? text : text[..(MaxBody - 1)] + "…";
}
