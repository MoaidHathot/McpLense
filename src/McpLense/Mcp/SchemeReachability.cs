using System.Net;

namespace McpLense;

/// <summary>
/// Lightweight "does this endpoint answer?" probe used by <see cref="TargetResolver"/> to decide
/// whether an inferred <c>https://</c> URL should fall back to <c>http://</c>. It is deliberately
/// minimal - a single short-timeout request that treats ANY HTTP response (including 4xx/5xx and
/// redirects) as "reachable", and only a transport failure (DNS, TLS handshake, refused, timeout)
/// as "not reachable". It never throws.
/// </summary>
internal static class SchemeReachability
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static async Task<bool> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // We only care whether the transport comes up; don't chase auth or content.
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = ProbeTimeout
        };
        using var client = new HttpClient(handler) { Timeout = ProbeTimeout };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            // HEAD is cheapest; a server that rejects HEAD still answers (which is all we need).
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            // Any status means the host is up and speaking HTTP on this scheme/port.
            return true;
        }
        catch (Exception)
        {
            // DNS failure, TLS handshake failure, connection refused, timeout, protocol mismatch:
            // treat as unreachable so the caller can try the other scheme.
            return false;
        }
    }
}
