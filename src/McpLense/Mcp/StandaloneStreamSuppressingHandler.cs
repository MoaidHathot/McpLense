namespace McpLense;

/// <summary>
/// Declines the Streamable HTTP "standalone" GET event-stream that the MCP SDK opens for unsolicited
/// server-&gt;client messages, by answering it locally with <c>405 Method Not Allowed</c> - exactly what a
/// server that doesn't offer the optional stream would return, so the SDK simply proceeds without it.
///
/// <para>
/// Why: some Streamable HTTP servers only commit the session asynchronously after the <c>initialize</c>
/// response is sent. The SDK opens the standalone GET stream immediately afterwards; against such a
/// server it arrives before the session exists and the server discards the just-created session,
/// breaking every subsequent request with <c>-32001 "Session not found"</c> (observed against
/// "CVM Triage MCP Bridge" - an Azure-hosted Kestrel MCP server). McpLense is request/response oriented
/// (tool/resource/prompt results - including tool-call progress - arrive on each POST's own response
/// stream), so the standalone push channel is not needed for inspect / list / call / read / prompt.
/// </para>
///
/// <para>
/// The suppression is deliberately narrow: it only fires for a GET that carries an <c>Mcp-Session-Id</c>
/// header, which uniquely identifies the Streamable HTTP standalone stream. The legacy HTTP+SSE
/// transport's primary GET stream carries no session header and is therefore left untouched.
/// </para>
/// </summary>
internal sealed class StandaloneStreamSuppressingHandler : DelegatingHandler
{
    private const string McpSessionHeader = "Mcp-Session-Id";

    public StandaloneStreamSuppressingHandler()
    {
    }

    public StandaloneStreamSuppressingHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    /// <summary>True when the request is the Streamable HTTP standalone GET event-stream.</summary>
    internal static bool IsStandaloneStreamRequest(HttpRequestMessage request)
        => request.Method == HttpMethod.Get && request.Headers.Contains(McpSessionHeader);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsStandaloneStreamRequest(request))
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.MethodNotAllowed)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            });
        }

        return base.SendAsync(request, cancellationToken);
    }
}
