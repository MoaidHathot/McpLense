using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpLense.Scanning.TargetResolution;

namespace McpLense;

public enum ConnectionKind
{
    Stdio,
    Http
}

/// <summary>Stable wire identifiers for <see cref="ConnectionAuthInfo.Mode"/>.</summary>
internal static class ConnectionAuthModes
{
    /// <summary>Authentication does not apply (stdio target).</summary>
    public const string None = "none";

    /// <summary>The server was reached without sending any credentials.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>The connection carried credentials (inline token or a profile).</summary>
    public const string Authenticated = "authenticated";
}

/// <summary>
/// How the live connection to a server actually authenticated. Captured at connect time so callers
/// can show whether we got in anonymously or with a profile, and which one. String-typed
/// <see cref="Mode"/> (see <see cref="ConnectionAuthModes"/>) keeps the JSON wire shape stable.
/// </summary>
/// <param name="Mode">One of <see cref="ConnectionAuthModes"/>.</param>
/// <param name="Profile">Profile name used, when authenticated via a profile (null for inline/anonymous).</param>
/// <param name="Kind">Auth scheme used (e.g. <c>Bearer</c>, <c>AzureCli</c>), when authenticated.</param>
/// <param name="Source">How the credentials were chosen: <c>inline</c>, <c>profile</c>, or <c>auto-pick</c>.</param>
internal sealed record ConnectionAuthInfo(
    string Mode,
    string? Profile = null,
    string? Kind = null,
    string? Source = null)
{
    public static readonly ConnectionAuthInfo None = new(ConnectionAuthModes.None);
    public static readonly ConnectionAuthInfo Anonymous = new(ConnectionAuthModes.Anonymous);

    public static ConnectionAuthInfo Authenticated(string? profile, AuthKind kind, string source)
        => new(ConnectionAuthModes.Authenticated, profile, kind.ToString(), source);
}

public sealed record ResolvedServer(
    string Name,
    ConnectionKind Kind,
    string Target,
    string? Source,
    string? Command,
    IReadOnlyList<string> CommandArguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    Uri? Url,
    TransportPreference Transport,
    IReadOnlyDictionary<string, string> Headers,
    ResolvedAuth? Auth = null,
    TargetScope HeaderScope = TargetScope.All,
    IReadOnlyList<string>? DisabledChecks = null,
    TimeSpan? HandshakeTimeout = null,
    ResolvedAuth? CandidateAuth = null,
    string? AuthProfileName = null);

internal sealed record ExecutionOutcome(object Payload, bool HasErrors);

internal sealed record InspectReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerInspection> Servers);

internal sealed record ServerInspection(
    string Name,
    string Transport,
    string Target,
    CapabilitySnapshot Capabilities,
    SectionResult<ToolInfo> Tools,
    SectionResult<ResourceInfo> Resources,
    SectionResult<ResourceTemplateInfo> ResourceTemplates,
    SectionResult<PromptInfo> Prompts,
    string? Error = null,
    ConnectionAuthInfo? AuthStatus = null);

internal sealed record CapabilitySnapshot(bool Tools, bool Resources, bool Prompts, bool Logging, bool Completions);

internal sealed record SectionResult<T>(bool Supported, IReadOnlyList<T> Items, string? Error = null);

internal sealed record ToolListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerItems<ToolInfo>> Servers);

internal sealed record ResourceListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerResources> Servers);

internal sealed record PromptListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerItems<PromptInfo>> Servers);

internal sealed record ServerItems<T>(string Name, string Transport, string Target, IReadOnlyList<T> Items, string? Error = null);

/// <summary>
/// Result of <c>mcplense resources</c> for one server: concrete resources (serialised as
/// <c>items</c> for backward compatibility) plus the server's resource templates.
/// </summary>
internal sealed record ServerResources(
    string Name,
    string Transport,
    string Target,
    IReadOnlyList<ResourceInfo> Items,
    IReadOnlyList<ResourceTemplateInfo> Templates,
    string? Error = null);

internal sealed record ToolCallReport(
    DateTimeOffset GeneratedAt,
    ServerReference Server,
    string ToolName,
    JsonObject? Arguments,
    IReadOnlyList<ProgressUpdate> Progress,
    CallResultView? Result,
    string? Error = null);

internal sealed record ReadReport(
    DateTimeOffset GeneratedAt,
    ServerReference Server,
    string Resource,
    JsonObject? Arguments,
    ReadResourceView? Result,
    string? Error = null);

internal sealed record PromptCallReport(
    DateTimeOffset GeneratedAt,
    ServerReference Server,
    string PromptName,
    JsonObject? Arguments,
    PromptResultView? Result,
    string? Error = null);

internal sealed record ServerReference(string Name, string Transport, string Target);

internal sealed record ToolInfo(string Name, string? Description, JsonNode? InputSchema);

internal sealed record ResourceInfo(string? Name, string? Uri, string? MimeType, string? Description);

internal sealed record ResourceTemplateInfo(string? Name, string? UriTemplate, string? MimeType, string? Description);

internal sealed record PromptInfo(string Name, string? Description, IReadOnlyList<PromptArgumentInfo> Arguments);

internal sealed record PromptArgumentInfo(string? Name, string? Description, bool Required);

internal sealed record ProgressUpdate(double? Progress, double? Total, string? Message, DateTimeOffset Timestamp);

internal sealed record CallResultView(
    bool? IsError,
    JsonNode? StructuredContent,
    JsonNode? Meta,
    IReadOnlyList<ContentBlockView> Content);

internal sealed record ReadResourceView(IReadOnlyList<ResourceContentView> Contents);

internal sealed record PromptResultView(string? Description, IReadOnlyList<PromptMessageView> Messages);

internal sealed record PromptMessageView(string? Role, ContentBlockView? Content);

internal sealed record ContentBlockView(
    string Kind,
    string? Text = null,
    string? MimeType = null,
    string? DataBase64 = null,
    int? ByteCount = null,
    ResourceContentView? Resource = null,
    JsonNode? Raw = null);

internal sealed record ResourceContentView(
    string Kind,
    string? Uri = null,
    string? MimeType = null,
    string? Text = null,
    string? DataBase64 = null,
    int? ByteCount = null,
    JsonNode? Raw = null);

// -------- Auth scan reports ----------------------------------------------------------

/// <summary>
/// Stable wire identifier for an auth-scan classification. String-typed rather than enum-typed
/// so the JSON output stays stable across mcplense versions even when new classifications are
/// added.
/// </summary>
internal static class AuthClassifications
{
    /// <summary>Stdio target - HTTP auth doesn't apply.</summary>
    public const string Stdio = "stdio";

    /// <summary>The HTTP probe reached the server cleanly without an auth challenge.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>Server advertises RFC 9728 Protected Resource Metadata pointing at OAuth.</summary>
    public const string OAuthRfc9728 = "oauth-rfc9728";

    /// <summary>
    /// Server demands auth (401 / WWW-Authenticate present) but doesn't advertise RFC 9728
    /// metadata - the client has to know out-of-band how to authenticate.
    /// </summary>
    public const string OAuthBearerUnannounced = "oauth-bearer-unannounced";

    /// <summary>
    /// Server demands auth with a non-Bearer scheme (Basic, Digest, NTLM, ...) or some other
    /// out-of-protocol mechanism not covered by MCP's OAuth profile.
    /// </summary>
    public const string AuthRequiredUnspecified = "auth-required-unspecified";

    /// <summary>Probe was inconclusive (network failure, 5xx, etc.); we can't say.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Top-level report emitted by <c>mcplense scan</c>.</summary>
internal sealed record AuthScanReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ServerAuthScan> Servers);

/// <summary>
/// Stable wire identifiers for <see cref="ServerAuthScan.ServerStatus"/> - a coarse,
/// consumer-friendly "can I talk to this server, and if so under what conditions?" enum.
/// String-typed (not a C# enum) for the same forward-compat reason as
/// <see cref="AuthClassifications"/>: new values must not break existing JSON consumers.
/// Derived from the raw transport + auth signals so every fleet-classifier doesn't have to
/// re-implement the same disambiguation logic.
/// </summary>
internal static class ServerAccessibility
{
    /// <summary>Server answered the unauthenticated MCP handshake; no credentials needed.</summary>
    public const string Accessible = "accessible";

    /// <summary>Server is reachable and asks for credentials (any flavour).</summary>
    public const string RequiresAuth = "requires-auth";

    /// <summary>Probe reached the server and got a 404 / 410.</summary>
    public const string NotFound = "not-found";

    /// <summary>Probe could not reach the server at all (DNS, TLS, connect, timeout, ...).</summary>
    public const string Unreachable = "unreachable";

    /// <summary>Not enough signal to classify (probe inconclusive, handshake non-auth failure, ...).</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Per-server outcome from <c>mcplense scan</c>. Carries the classification, the raw signals
/// that produced it, and one <see cref="ProfileAttempt"/> entry per profile actually exercised.
/// </summary>
/// <param name="ServerStatus">
/// Coarse reachability/auth label derived from the raw probe + handshake signals. See
/// <see cref="ServerAccessibility"/> for the stable wire values. Lets fleet consumers
/// classify without re-implementing the transport-status / auth-challenge / handshake
/// disambiguation themselves.
/// </param>
/// <param name="Rfcs">
/// RFC numbers implicated by the classification (e.g. <c>"RFC 9728"</c>, <c>"RFC 6750"</c>,
/// <c>"RFC 8414"</c>, <c>"RFC 7591"</c>). Empty when no auth RFCs apply (anonymous /
/// non-Bearer). Mapped from <see cref="Classification"/> + <see cref="Details"/>; consumers
/// that need finer detail should still pattern-match on the raw signals.
/// </param>
internal sealed record ServerAuthScan(
    string Name,
    string Transport,
    string Target,
    string Classification,
    string Summary,
    AuthScanDetails Details,
    IReadOnlyList<ProfileAttempt> ProfileAttempts,
    string ServerStatus = ServerAccessibility.Unknown,
    IReadOnlyList<string>? Rfcs = null,
    string? Error = null);

/// <summary>
/// Raw signals that produced an <see cref="ServerAuthScan.Classification"/>. Most fields are
/// optional because the underlying probe may not surface them (anonymous servers, network
/// failures, non-Bearer challenges, etc.).
/// </summary>
/// <param name="ReasonPhrase">
/// HTTP reason phrase from the unauthenticated probe response (e.g. "Unauthorized",
/// "Forbidden"). Preserved verbatim alongside <see cref="StatusCode"/> because some servers
/// embed actionable error context here (e.g. Agent365 / Microsoft endpoints sometimes ship
/// custom phrases like "Tenant restricted").
/// </param>
/// <param name="DiagnosticHeaders">
/// Verbatim copy of well-known diagnostic response headers (X-MS-Diagnostics, X-Trace-Id,
/// X-Correlation-Id, ...) emitted by the server. Populated only when at least one such header
/// is present. Microsoft endpoints in particular carry actionable error info here that would
/// otherwise be lost.
/// </param>
internal sealed record AuthScanDetails(
    int? StatusCode = null,
    string? ReasonPhrase = null,
    string? WwwAuthenticate = null,
    string? ResourceMetadataUrl = null,
    string? Resource = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? AuthorizationServers = null,
    bool? AnonymousHandshakeSucceeded = null,
    string? AnonymousHandshakeError = null,
    string? ProbeError = null,
    IReadOnlyDictionary<string, string>? DiagnosticHeaders = null);

/// <summary>
/// Outcome of attempting to open an MCP session against the target using a specific profile.
/// </summary>
/// <param name="ProfileName">Profile name from the loaded set.</param>
/// <param name="AuthKind">Profile's auth kind (bearer/oauth/interactive-browser/azure-cli).</param>
/// <param name="Scopes">Scopes actually requested (after probe-based substitution), if any.</param>
/// <param name="Success">True when the MCP <c>initialize</c> handshake completed.</param>
/// <param name="Detail">
/// Human-readable note when <paramref name="Success"/> is true (e.g. capability summary).
/// </param>
/// <param name="Error">Failure reason when <paramref name="Success"/> is false.</param>
/// <param name="ToolCount">Number of tools the server listed, when the handshake succeeded.</param>
/// <param name="ResourceCount">Number of resources the server listed, when the handshake succeeded.</param>
/// <param name="PromptCount">Number of prompts the server listed, when the handshake succeeded.</param>
internal sealed record ProfileAttempt(
    string ProfileName,
    string AuthKind,
    IReadOnlyList<string>? Scopes,
    bool Success,
    string? Detail = null,
    string? Error = null,
    int? ToolCount = null,
    int? ResourceCount = null,
    int? PromptCount = null);

// -------- Audit report (mcplense scan) ---------------------------------------------

/// <summary>
/// Verbatim copy of MCP's <c>tools/list</c> annotation hints. Every field is nullable so the
/// scan can distinguish "the server didn't say" from "the server said false". The Tier 1
/// schema-fingerprint and the per-tool missing-annotations list live on the actual
/// <c>tools</c> check's output records (<c>ToolsCheck.ToolEntryExtended</c>) - this record is
/// the shared shape kept here because multiple checks reference it.
/// </summary>
internal sealed record ToolAnnotationsView(
    string? Title,
    bool? ReadOnlyHint,
    bool? DestructiveHint,
    bool? IdempotentHint,
    bool? OpenWorldHint);

internal sealed record ToolsCapabilityView(bool? ListChanged);
internal sealed record PromptsCapabilityView(bool? ListChanged);
internal sealed record ResourcesCapabilityView(bool? ListChanged, bool? Subscribe);

/// <summary>Empty marker for capabilities that have no documented sub-fields today (e.g. <c>logging</c>).</summary>
internal sealed record CapabilityFlagView();

/// <summary>
/// TLS certificate fields captured during the unauthenticated probe. Values come from the
/// leaf certificate on the chain; we never validate or judge them - that's policy. Days-until
/// -expiry is a derived value, but it's a fact (date arithmetic), not a label.
/// </summary>
internal sealed record TlsInfo(
    string? Subject,
    string? Issuer,
    string? Thumbprint,
    string? SerialNumber,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    int? DaysUntilExpiry,
    string? SignatureAlgorithm,
    IReadOnlyList<string> SubjectAlternativeNames,
    string? ProtocolVersion);

/// <summary>
/// Security-relevant HTTP response headers from the unauthenticated probe. All fields are
/// nullable / empty when the server didn't emit the header; consumers can pattern-match on
/// presence/absence directly.
/// </summary>
internal sealed record ResponseHeadersSummary(
    string? Server,
    string? XPoweredBy,
    string? StrictTransportSecurity,
    string? ContentSecurityPolicy,
    string? XFrameOptions,
    string? XContentTypeOptions,
    string? ReferrerPolicy,
    string? AccessControlAllowOrigin,
    string? AccessControlAllowCredentials,
    string? CacheControl,
    IReadOnlyDictionary<string, string> Other);

/// <summary>
/// Dynamic Client Registration (RFC 7591) endpoint observations.
/// </summary>
internal sealed record DcrInfo(
    string? Endpoint,
    bool? OpenRegistration);

/// <summary>
/// Per-authorization-server RFC 8414 metadata snapshot (when fetched). Captures the most
/// security-relevant fields verbatim plus a copy of the full document under <see cref="Raw"/>
/// for forensic / consumer-side analysis.
/// </summary>
internal sealed record AuthorizationServerInfo(
    string Issuer,
    bool Fetched,
    string? FetchError,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? RegistrationEndpoint,
    string? IntrospectionEndpoint,
    string? RevocationEndpoint,
    string? JwksUri,
    IReadOnlyList<string> ScopesSupported,
    IReadOnlyList<string> ResponseTypesSupported,
    IReadOnlyList<string> GrantTypesSupported,
    IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
    IReadOnlyList<string> CodeChallengeMethodsSupported,
    bool? ResourceParameterSupported,
    JsonNode? Raw);

/// <summary>
/// Result of calling a tool name the server (presumably) doesn't expose. Three outcome
/// shapes, kept structurally distinct so consumers can pattern-match on the response category:
/// <list type="bullet">
///   <item><description><c>tool-result-returned</c>: the server replied with a normal JSON-RPC
///   success carrying a tool result envelope. <see cref="ToolResultIsError"/> reflects the
///   <c>isError</c> flag on that envelope; <see cref="ToolResultJson"/> holds the verbatim
///   serialised result so consumers can inspect what the server leaked back.</description></item>
///   <item><description><c>jsonrpc-error</c>: the server returned a JSON-RPC error response;
///   <see cref="JsonRpcErrorCode"/> / <see cref="JsonRpcErrorMessage"/> /
///   <see cref="JsonRpcErrorData"/> carry the verbatim error fields.</description></item>
///   <item><description><c>transport-error</c>: the call never reached or could not be parsed
///   from the server; <see cref="TransportError"/> carries the framework exception.</description></item>
/// </list>
/// </summary>
internal sealed record CallNonExistentToolProbe(
    bool Attempted,
    string ToolNameUsed,
    string? FetchedVia,
    string Outcome,
    bool? ToolResultIsError = null,
    string? ToolResultJson = null,
    int? JsonRpcErrorCode = null,
    string? JsonRpcErrorMessage = null,
    JsonNode? JsonRpcErrorData = null,
    string? TransportError = null);

/// <summary>Stable wire identifiers for <see cref="CallNonExistentToolProbe.Outcome"/>.</summary>
internal static class CallNonExistentToolOutcomes
{
    public const string ToolResultReturned = "tool-result-returned";
    public const string JsonRpcError = "jsonrpc-error";
    public const string TransportError = "transport-error";
}
