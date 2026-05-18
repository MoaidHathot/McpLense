using System.Text.Json.Nodes;

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
