namespace McpLense;

/// <summary>
/// Identifies how mcplense authenticates outbound HTTP requests to an MCP server.
/// </summary>
internal enum AuthKind
{
    /// <summary>No authentication. Outbound requests carry no <c>Authorization</c> header.</summary>
    None,

    /// <summary>Static <c>Authorization: Bearer &lt;token&gt;</c> header.</summary>
    Bearer,

    /// <summary>
    /// MCP-spec OAuth 2.1 with discovery (RFC 9728 / RFC 8414), PKCE (RFC 7636),
    /// and Dynamic Client Registration (RFC 7591). Implemented in Slice B.
    /// </summary>
    OAuth,

    /// <summary>
    /// Microsoft Entra ID interactive-browser auth backed by MSAL via
    /// <see cref="Azure.Identity.InteractiveBrowserCredential"/>. Targets pre-registered public
    /// clients (typically the VS Code first-party client) and persists tokens in the OS
    /// credential store. Bypasses RFC 8414 / DCR / loopback-callback handling.
    /// </summary>
    InteractiveBrowser
}

/// <summary>
/// Auth configuration resolved from a config file and/or CLI overrides for a single server.
/// All string-typed fields have already been environment-expanded.
/// </summary>
/// <param name="Kind">Auth scheme to use.</param>
/// <param name="Token">Static bearer token (when <see cref="Kind"/> is <see cref="AuthKind.Bearer"/>).</param>
/// <param name="Scopes">OAuth scopes to request.</param>
/// <param name="RedirectUri">
/// Override for the loopback redirect URI used during the authorization-code flow.
/// When null, the orchestrator picks an OS-assigned port on <c>127.0.0.1</c>.
/// For <see cref="AuthKind.InteractiveBrowser"/>, when null MSAL picks an OS-assigned port on
/// <c>http://localhost</c> (the only loopback host Entra's exception covers).
/// </param>
/// <param name="CacheName">
/// Override token-cache key. When null for OAuth, a stable hash of the resource URI is used.
/// For <see cref="AuthKind.InteractiveBrowser"/>, this is the MSAL cache file name (defaults to
/// <c>"mcplense"</c>); set to <c>"mcp-proxy"</c> to share the cache with the mcp-proxy tool.
/// </param>
/// <param name="ClientId">
/// Pre-registered OAuth client id; bypasses Dynamic Client Registration when set.
/// Required for <see cref="AuthKind.InteractiveBrowser"/>.
/// </param>
/// <param name="ClientSecret">Optional client secret for confidential clients (rare for native apps).</param>
/// <param name="TenantId">
/// Entra tenant identifier (GUID, domain, or one of <c>common</c>/<c>organizations</c>/<c>consumers</c>).
/// Only meaningful for <see cref="AuthKind.InteractiveBrowser"/>; when null MSAL defaults to
/// <c>common</c>.
/// </param>
/// <param name="Issuer">
/// Authorization-server issuer URL. When set, discovery is performed against this URL directly
/// instead of via Protected Resource Metadata.
/// </param>
/// <param name="AuthorizationEndpoint">Static authorization endpoint; bypasses ASM discovery when set.</param>
/// <param name="TokenEndpoint">Static token endpoint; bypasses ASM discovery when set.</param>
/// <param name="RegistrationEndpoint">Static DCR endpoint; bypasses ASM discovery when set.</param>
/// <param name="ResourceMetadataUrl">
/// Static Protected Resource Metadata URL. Defaults to <c>{resource}/.well-known/oauth-protected-resource</c>.
/// </param>
/// <param name="ResourceUri">
/// RFC 8707 resource indicator. Defaults to the MCP server URL when null.
/// </param>
internal sealed record ResolvedAuth(
    AuthKind Kind,
    string? Token = null,
    IReadOnlyList<string>? Scopes = null,
    string? RedirectUri = null,
    string? CacheName = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? TenantId = null,
    string? Issuer = null,
    string? AuthorizationEndpoint = null,
    string? TokenEndpoint = null,
    string? RegistrationEndpoint = null,
    string? ResourceMetadataUrl = null,
    string? ResourceUri = null);

/// <summary>
/// CLI-provided overlay applied on top of (or in place of) the per-server <c>auth</c> block
/// resolved from a config file. <see cref="NoAuth"/> trumps every other field.
/// </summary>
/// <param name="Kind">Auth scheme to use; replaces the config <c>auth</c> block when set.</param>
/// <param name="Token">Bearer token override.</param>
/// <param name="Scopes">OAuth scopes override.</param>
/// <param name="RedirectUri">Loopback redirect URI override.</param>
/// <param name="CacheName">Token-cache name override.</param>
/// <param name="ClientId">
/// Pre-registered client id override. Used by both <see cref="AuthKind.OAuth"/> and
/// <see cref="AuthKind.InteractiveBrowser"/>.
/// </param>
/// <param name="TenantId">
/// Entra tenant override. Only consumed by <see cref="AuthKind.InteractiveBrowser"/>.
/// </param>
/// <param name="NoAuth">Suppress all authentication (HTTP and stdio).</param>
/// <param name="LoginOnly">
/// When true, the CLI runs the OAuth flow once and writes the resulting token to the cache,
/// then exits 0 without dispatching the underlying command.
/// </param>
/// <param name="LogoutOnly">
/// When true, the CLI clears the cached OAuth tokens for the resolved server(s)
/// then exits 0 without dispatching the underlying command.
/// </param>
internal sealed record AuthOverrides(
    AuthKind? Kind = null,
    string? Token = null,
    IReadOnlyList<string>? Scopes = null,
    string? RedirectUri = null,
    string? CacheName = null,
    string? ClientId = null,
    string? TenantId = null,
    bool NoAuth = false,
    bool LoginOnly = false,
    bool LogoutOnly = false)
{
    public static readonly AuthOverrides Empty = new();

    /// <summary>True when no override was supplied at all (CLI did not set any auth flag).</summary>
    public bool IsEmpty
        => !NoAuth
           && !LoginOnly
           && !LogoutOnly
           && Kind is null
           && Token is null
           && (Scopes is null || Scopes.Count == 0)
           && RedirectUri is null
           && CacheName is null
           && ClientId is null
           && TenantId is null;
}

/// <summary>
/// Surface-level exception raised when authentication setup fails (factory, handler, cache, etc.).
/// Propagates to <see cref="App"/>'s top-level error handler and renders as a 1-line stderr message.
/// </summary>
internal sealed class McpLenseAuthException : Exception
{
    public McpLenseAuthException(string message)
        : base(message)
    {
    }

    public McpLenseAuthException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
