using System.Text;

namespace McpLense.Scanning.Checks.Behavior;

/// <summary>
/// Sends deliberately malformed JSON-RPC to the HTTP MCP endpoint and records how the server
/// responds - a robustness signal (a well-behaved server returns a parse/validation error; a fragile
/// one may 5xx, hang, or leak internals). Outbound + intentionally malformed, so it is opt-in
/// (default off). HTTP only; stdio targets are skipped.
/// </summary>
internal sealed class CallMalformedCheck : IScanCheck
{
    public string Id => "behavior.callMalformed";
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public bool IsEnabledByDefault => false;

    private static readonly (string Case, string Body)[] Payloads =
    [
        ("invalid-json", "{ this is not valid json "),
        ("valid-json-not-jsonrpc", "{\"hello\":\"world\"}"),
        ("jsonrpc-missing-method", "{\"jsonrpc\":\"2.0\",\"id\":1}")
    ];

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var server = context.Server;
        if (server.Kind != ConnectionKind.Http || server.Url is null)
        {
            return CheckOutcome.Skipped;
        }

        using var http = McpHttpClientFactory.Create(server, server.Auth);
        var probes = new List<MalformedProbe>(Payloads.Length);

        foreach (var (name, body) in Payloads)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(context.HandshakeTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

                using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
                string? excerpt = null;
                try
                {
                    excerpt = Excerpt(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                }
                catch
                {
                    // streaming/unreadable body; status is enough
                }

                probes.Add(new MalformedProbe(name, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, excerpt, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                probes.Add(new MalformedProbe(name, null, null, null, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new MalformedData(Attempted: true, Probes: probes)), Error: null);
    }

    private static string Excerpt(string body)
    {
        var trimmed = body.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..199] + "…";
    }

    internal sealed record MalformedData(bool Attempted, IReadOnlyList<MalformedProbe> Probes);

    internal sealed record MalformedProbe(string Case, int? StatusCode, string? ContentType, string? ResponseExcerpt, string? TransportError);
}
