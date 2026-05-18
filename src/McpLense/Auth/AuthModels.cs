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
    /// and Dynamic Client Registration (RFC 7591).
    /// </summary>
    OAuth,

    /// <summary>
    /// Microsoft Entra ID interactive-browser auth backed by MSAL via
    /// <see cref="Azure.Identity.InteractiveBrowserCredential"/>. Targets pre-registered public
    /// clients (typically the VS Code first-party client) and persists tokens in the OS
    /// credential store. Bypasses RFC 8414 / DCR / loopback-callback handling.
    /// </summary>
    InteractiveBrowser,

    /// <summary>
    /// Microsoft Entra ID via the Azure CLI. Delegates token acquisition to
    /// <see cref="Azure.Identity.AzureCliCredential"/>, which shells out to
    /// <c>az account get-access-token --resource &lt;scope&gt;</c> using the user's existing
    /// <c>az login</c> session. No interactive browser pop; ideal for headless and CI
    /// scenarios where the user (or the agent) is already authenticated to the Azure CLI.
    /// Requires the Azure CLI to be installed and on PATH, and a prior <c>az login</c>.
    /// </summary>
    AzureCli
}

/// <summary>
/// Auth configuration resolved from a profile file (or derived from CLI ad-hoc overrides for
/// simple Bearer cases). All string-typed fields have already been environment-expanded.
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
/// For <see cref="AuthKind.InteractiveBrowser"/>, this is the MSAL cache file name. Profiles
/// default this to the profile name so each profile gets its own cache.
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
/// CLI-provided overlay describing how to handle authentication for the resolved target(s).
/// In Phase A the per-field auth knobs (clientId/tenantId/scopes/redirectUri/cacheName) were
/// removed: rich auth lives in named profiles, while the CLI exposes only profile selection
/// plus the simple ad-hoc Bearer escape hatch. Phase C added the <see cref="All"/> field used
/// by the top-level <c>mcplense login</c> / <c>mcplense logout</c> commands.
/// </summary>
/// <param name="Kind">
/// Auth scheme to apply ad-hoc (only <see cref="AuthKind.Bearer"/> is supported here; richer
/// schemes must come from a profile).
/// </param>
/// <param name="Token">Bearer token paired with <paramref name="Kind"/> = Bearer.</param>
/// <param name="Profile">
/// Profile name forced via <c>--profile</c>. When set, profile auto-selection is skipped and the
/// resolver looks up this exact profile by name.
/// </param>
/// <param name="TryAll">
/// When true, the resolver walks every loaded profile sequentially (prompting interactively as
/// needed) instead of auto-picking. Mutually exclusive with <see cref="Profile"/>.
/// </param>
/// <param name="All">
/// When true, the top-level <c>mcplense login</c> / <c>mcplense logout</c> commands act on
/// every loaded profile. Mutually exclusive with <see cref="Profile"/>.
/// </param>
/// <param name="NoAuth">Suppress all authentication (HTTP and stdio).</param>
/// <param name="ClassifyOnly">
/// Scan-only flag. When true, <c>mcplense scan</c> emits the auth-classification block (probe
/// status, RFC 9728 metadata, etc.) and skips profile attempts entirely. Differs from
/// <see cref="NoAuth"/> only in that <see cref="NoAuth"/> is the broader "strip authentication
/// from every command" toggle (it also wipes inline auth on resolved servers in
/// <c>inspect</c>, <c>tools</c>, etc.), whereas <see cref="ClassifyOnly"/> is scoped to
/// <c>scan</c> and rejected on other commands. Both produce the same scan output; offer
/// <c>--classify-only</c> to users who want a discoverable, scan-specific name.
/// </param>
internal sealed record AuthOverrides(
    AuthKind? Kind = null,
    string? Token = null,
    string? Profile = null,
    bool TryAll = false,
    bool All = false,
    bool NoAuth = false,
    bool ClassifyOnly = false)
{
    public static readonly AuthOverrides Empty = new();

    /// <summary>True when no override was supplied at all (CLI did not set any auth flag).</summary>
    public bool IsEmpty
        => !NoAuth
           && !TryAll
           && !All
           && !ClassifyOnly
           && Kind is null
           && Token is null
           && Profile is null;
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
