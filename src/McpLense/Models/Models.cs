using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace McpLense;

internal enum ConnectionKind
{
    Stdio,
    Http
}

internal sealed record ResolvedServer(
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
    ResolvedAuth? Auth = null);

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
    string? Error = null);

internal sealed record CapabilitySnapshot(bool Tools, bool Resources, bool Prompts, bool Logging, bool Completions);

internal sealed record SectionResult<T>(bool Supported, IReadOnlyList<T> Items, string? Error = null);

internal sealed record ToolListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerItems<ToolInfo>> Servers);

internal sealed record ResourceListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerItems<ResourceInfo>> Servers);

internal sealed record PromptListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerItems<PromptInfo>> Servers);

internal sealed record ServerItems<T>(string Name, string Transport, string Target, IReadOnlyList<T> Items, string? Error = null);

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
/// Per-server outcome from <c>mcplense scan</c>. Carries the classification, the raw signals
/// that produced it, and one <see cref="ProfileAttempt"/> entry per profile actually exercised.
/// </summary>
internal sealed record ServerAuthScan(
    string Name,
    string Transport,
    string Target,
    string Classification,
    string Summary,
    AuthScanDetails Details,
    IReadOnlyList<ProfileAttempt> ProfileAttempts,
    string? Error = null);

/// <summary>
/// Raw signals that produced an <see cref="ServerAuthScan.Classification"/>. Most fields are
/// optional because the underlying probe may not surface them (anonymous servers, network
/// failures, non-Bearer challenges, etc.).
/// </summary>
internal sealed record AuthScanDetails(
    int? StatusCode = null,
    string? WwwAuthenticate = null,
    string? ResourceMetadataUrl = null,
    string? Resource = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? AuthorizationServers = null,
    bool? AnonymousHandshakeSucceeded = null,
    string? AnonymousHandshakeError = null,
    string? ProbeError = null);

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
/// Top-level report emitted by <c>mcplense scan</c>. Bundles the auth classification
/// (<see cref="ServerAuthScan"/>) with every other surface mcplense can observe statically:
/// server identity, protocol details, advertised capabilities, full tool/prompt/resource
/// enumeration when reachable, TLS posture, security-relevant response headers, OAuth
/// authorization-server discovery (opt-in), behaviour probes, and stdio configuration.
/// </summary>
/// <remarks>
/// The audit is deliberately fact-only: it does not attempt to label findings as
/// "dangerous", "safe", "high-risk", etc. Every field is a raw observation; consumers (humans
/// or downstream tooling) apply policy on top. The two exceptions are factual lists:
/// <list type="bullet">
///   <item><description><c>missingAnnotations</c> per tool (which annotations the server did
///   <em>not</em> declare).</description></item>
///   <item><description><c>fetchedVia</c> on each enumeration (which path produced the list).</description></item>
/// </list>
/// Both are descriptions, not judgements.
/// </remarks>
internal sealed record AuditReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ServerAudit> Servers);

internal sealed record ServerAudit(
    string Name,
    string Transport,
    string Target,
    ServerAuthScan Auth,
    ServerInfoSummary? ServerInfo,
    ProtocolSummary? Protocol,
    ToolListing Tools,
    PromptListing Prompts,
    ResourceListing Resources,
    SecuritySummary Security,
    // Default camelCase naming policy lowercases only the first character, which would emit
    // "oAuth" - ugly and grep-hostile. Pin the wire name to "oauth" so consumers can
    // pattern-match on the obvious key.
    [property: JsonPropertyName("oauth")] OAuthSummary? OAuth,
    BehaviorProbes Behavior,
    StdioSummary? Stdio,
    string? Error = null);

/// <summary>Server identification reported in the <c>initialize</c> response (<c>Implementation</c>).</summary>
internal sealed record ServerInfoSummary(
    string? Name,
    string? Title,
    string? Version,
    string? Description,
    string? WebsiteUrl,
    JsonNode? Meta = null);

/// <summary>
/// Protocol-level details: negotiated version, full advertised capability block (including
/// experimental + extensions), verbatim server instructions, and any top-level <c>_meta</c>.
/// Captured directly from the MCP <c>initialize</c> response so consumers can audit the
/// server's declared posture without inferring anything.
/// </summary>
internal sealed record ProtocolSummary(
    string? NegotiatedProtocolVersion,
    CapabilitiesView Capabilities,
    string? Instructions,
    int? InstructionsLength,
    JsonNode? Meta = null);

/// <summary>
/// Full advertised <c>ServerCapabilities</c> block. Each section is null when the server did
/// not advertise it; advertised but empty sub-records are kept as empty objects so consumers
/// can distinguish "not advertised" from "advertised, no sub-options".
/// </summary>
internal sealed record CapabilitiesView(
    ToolsCapabilityView? Tools,
    PromptsCapabilityView? Prompts,
    ResourcesCapabilityView? Resources,
    CapabilityFlagView? Logging,
    CapabilityFlagView? Completions,
    CapabilityFlagView? Tasks,
    JsonNode? Experimental,
    JsonNode? Extensions);

internal sealed record ToolsCapabilityView(bool? ListChanged);
internal sealed record PromptsCapabilityView(bool? ListChanged);
internal sealed record ResourcesCapabilityView(bool? ListChanged, bool? Subscribe);

/// <summary>Empty marker for capabilities that have no documented sub-fields today (e.g. <c>logging</c>).</summary>
internal sealed record CapabilityFlagView();

/// <summary>
/// Tool enumeration: every tool the server returned via <c>tools/list</c>, with verbatim
/// descriptions, schemas, and the MCP-spec annotations (read-only/destructive/idempotent/
/// open-world). <see cref="Fetched"/> is false when no auth path could reach the list.
/// </summary>
internal sealed record ToolListing(
    bool Fetched,
    string? FetchedVia,
    string? FetchError,
    IReadOnlyList<ToolEntry> Items);

internal sealed record ToolEntry(
    string Name,
    string? Title,
    string? Description,
    JsonNode? InputSchema,
    JsonNode? OutputSchema,
    ToolAnnotationsView? Annotations,
    IReadOnlyList<string> MissingAnnotations,
    JsonNode? Meta = null);

/// <summary>
/// Verbatim copy of MCP's <c>tools/list</c> annotation hints. Every field is nullable so the
/// scan can distinguish "the server didn't say" from "the server said false". Consumers can
/// read <see cref="ToolEntry.MissingAnnotations"/> to see which hints were omitted entirely.
/// </summary>
internal sealed record ToolAnnotationsView(
    string? Title,
    bool? ReadOnlyHint,
    bool? DestructiveHint,
    bool? IdempotentHint,
    bool? OpenWorldHint);

internal sealed record PromptListing(
    bool Fetched,
    string? FetchedVia,
    string? FetchError,
    IReadOnlyList<PromptEntry> Items);

internal sealed record PromptEntry(
    string Name,
    string? Title,
    string? Description,
    IReadOnlyList<PromptArgumentInfo> Arguments,
    JsonNode? Meta = null);

internal sealed record ResourceListing(
    bool Fetched,
    string? FetchedVia,
    string? FetchError,
    IReadOnlyList<ResourceEntry> Items,
    IReadOnlyList<ResourceTemplateEntry> Templates);

internal sealed record ResourceEntry(
    string? Name,
    string? Title,
    string? Uri,
    string? UriScheme,
    string? MimeType,
    long? Size,
    string? Description,
    JsonNode? Meta = null);

internal sealed record ResourceTemplateEntry(
    string? Name,
    string? Title,
    string? UriTemplate,
    string? MimeType,
    string? Description,
    JsonNode? Meta = null);

/// <summary>
/// Network / transport security observations. Empty (all-null) for stdio targets.
/// </summary>
internal sealed record SecuritySummary(
    bool MixedContent,
    TlsInfo? Tls,
    ResponseHeadersSummary? ResponseHeaders);

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
/// OAuth/OIDC posture observations. Always emitted when the auth classification is
/// <see cref="AuthClassifications.OAuthRfc9728"/>; <see cref="DcrFromResourceMetadata"/> comes
/// from the PRM document, while <see cref="AuthorizationServers"/> is populated only when
/// <c>--check-authorization-servers</c> is set and the AS metadata fetch succeeds.
/// </summary>
internal sealed record OAuthSummary(
    DcrInfo? DcrFromResourceMetadata,
    IReadOnlyList<AuthorizationServerInfo> AuthorizationServers);

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
/// Stdio surface for stdio targets: resolved command line, arguments, working directory, and
/// the (post-env-expansion) environment variables we'd pass to the child process. Empty for
/// HTTP targets.
/// </summary>
internal sealed record StdioSummary(
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Conformance / behaviour observations gathered by sending crafted MCP messages.
/// Each probe is its own optional record so consumers can pattern-match individually.
/// </summary>
internal sealed record BehaviorProbes(
    CallNonExistentToolProbe? CallNonExistentTool);

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
