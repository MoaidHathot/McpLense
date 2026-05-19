using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace McpLense.Scanning.Checks;

/// <summary>
/// CORS preflight against the MCP URL: one OPTIONS request with a synthetic Origin and a
/// requested method. Captures every Access-Control-* response header verbatim plus the
/// Allow / Vary headers. Default-on because the cost is a single extra GET-equivalent.
/// </summary>
internal sealed class CorsPreflightCheck : IScanCheck
{
    public string Id => "corsPreflight";
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Server.Kind != ConnectionKind.Http || context.Server.Url is null)
        {
            return CheckOutcome.Skipped;
        }

        // Prefer the shared HttpClient pool when available (DI-wired AddMcpLense or
        // ScanCommandDispatcher registered it). Fall back to a one-off HttpClient when the
        // host doesn't provide the factory (e.g. ScanPipelineBuilder.UseServices() called
        // with a minimal ServiceCollection).
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
            using var request = new HttpRequestMessage(HttpMethod.Options, context.Server.Url);
            request.Headers.TryAddWithoutValidation("Origin", "https://mcplense.invalid");
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "Content-Type, Authorization, MCP-Session-Id, MCP-Protocol-Version");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            string? Header(string name)
                => response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

            var data = new CorsPreflightData(
                StatusCode: (int)response.StatusCode,
                AccessControlAllowOrigin: Header("Access-Control-Allow-Origin"),
                AccessControlAllowMethods: Header("Access-Control-Allow-Methods"),
                AccessControlAllowHeaders: Header("Access-Control-Allow-Headers"),
                AccessControlAllowCredentials: Header("Access-Control-Allow-Credentials"),
                AccessControlMaxAge: Header("Access-Control-Max-Age"),
                AccessControlExposeHeaders: Header("Access-Control-Expose-Headers"),
                Allow: Header("Allow"),
                Vary: Header("Vary"));

            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(data), Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ownHttp)
            {
                http.Dispose();
            }
        }
    }

    internal sealed record CorsPreflightData(
        int StatusCode,
        string? AccessControlAllowOrigin,
        string? AccessControlAllowMethods,
        string? AccessControlAllowHeaders,
        string? AccessControlAllowCredentials,
        string? AccessControlMaxAge,
        string? AccessControlExposeHeaders,
        string? Allow,
        string? Vary);
}
