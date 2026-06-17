namespace McpLense;

/// <summary>
/// Single source of truth for the <see cref="HttpClient"/> stack behind every HTTP MCP connection:
/// the runtime commands, the live TUI session, the auth-scan handshake probe, and the scan
/// pipeline's session checks. Centralising it guarantees the standalone-stream suppression, the
/// auth-handler chaining, and the streaming-friendly infinite timeout are applied identically
/// everywhere - the per-call-site duplication this replaces is exactly what let earlier fixes reach
/// only some paths (e.g. <c>scan</c> session checks kept losing the session to the -32001 quirk).
/// </summary>
internal static class McpHttpClientFactory
{
    /// <summary>
    /// Builds the HTTP stack for an MCP transport.
    /// </summary>
    /// <param name="server">Resolved server (for the URL the auth handler binds to).</param>
    /// <param name="auth">Credentials to attach, or null for an unauthenticated client.</param>
    /// <param name="suppressStandaloneStream">
    /// When true (default) the SDK's optional standalone GET event-stream (the GET carrying an
    /// <c>Mcp-Session-Id</c>) is declined locally with a 405. Some Streamable HTTP servers commit
    /// the session only after <c>initialize</c>; the early standalone stream otherwise races ahead
    /// and the server discards the session, breaking every later request with
    /// <c>-32001 "Session not found"</c>. Pass false only when the caller genuinely needs the
    /// out-of-band server-&gt;client channel (the server-initiated observation scan check, or the
    /// runtime <c>--server-stream</c> opt-in), accepting that risk.
    /// </param>
    public static HttpClient Create(ResolvedServer server, ResolvedAuth? auth, bool suppressStandaloneStream = true)
    {
        // No client-level timeout: MCP rides long-lived SSE streams, so the per-operation deadline is
        // enforced by the caller's cancellation token (and HttpClientTransportOptions.ConnectionTimeout),
        // not HttpClient.Timeout, which would otherwise abort a streaming response mid-flight.
        return new HttpClient(BuildHandlerChain(server, auth, suppressStandaloneStream), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// Composes the handler chain (outermost first): optional <see cref="StandaloneStreamSuppressingHandler"/>,
    /// optional auth <see cref="DelegatingHandler"/>, then a <see cref="SocketsHttpHandler"/>. Exposed for
    /// tests that assert the composition without standing up an HttpClient.
    /// </summary>
    internal static HttpMessageHandler BuildHandlerChain(ResolvedServer server, ResolvedAuth? auth, bool suppressStandaloneStream)
    {
        HttpMessageHandler chain = new SocketsHttpHandler();

        if (auth is { Kind: not AuthKind.None } && AuthHandlerFactory.Create(auth, server.Url) is { } authHandler)
        {
            authHandler.InnerHandler = chain;
            chain = authHandler;
        }

        if (suppressStandaloneStream)
        {
            chain = new StandaloneStreamSuppressingHandler(chain);
        }

        return chain;
    }
}
