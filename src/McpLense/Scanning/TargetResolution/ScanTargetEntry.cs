using System.Text.Json.Serialization;

namespace McpLense.Scanning.TargetResolution;

/// <summary>
/// Scope of how the merged <see cref="ScanTargetEntry.Headers"/> apply when scanning the
/// matched MCP server.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TargetScope>))]
public enum TargetScope
{
    /// <summary>
    /// Headers apply to BOTH the MCP <c>initialize</c> session AND every same-origin
    /// HTTP probe (transport probe, CORS preflight, authenticated headers re-probe, DCR
    /// endpoint probe). Default.
    /// </summary>
    All,

    /// <summary>
    /// Headers apply ONLY to the MCP <c>initialize</c> session. Probes go out unauthenticated.
    /// Use when you specifically want the unauthenticated probe to stay unauthenticated, e.g.
    /// when validating that a server's bare <c>GET /mcp</c> still returns the WWW-Authenticate
    /// challenge.
    /// </summary>
    Session
}

/// <summary>
/// One declarative entry under the <c>targets</c> array in <c>McpLense.Config.json</c>.
/// Each entry binds an exact MCP URL (and optionally a short name for CLI lookup) to a set
/// of HTTP headers, an optional auth profile, transport / timeout overrides, and a list of
/// checks to skip for this server.
/// </summary>
/// <remarks>
/// Precedence: pattern entries are applied first (least specific), then named target entries
/// (more specific), then CLI flags (most specific). See <see cref="TargetOverlayResolver"/>
/// for the merge algorithm.
/// </remarks>
public sealed class ScanTargetEntry
{
    /// <summary>
    /// Optional short identifier the user can pass on the CLI as <c>@name</c> in place of
    /// the URL. Names are matched case-insensitively. Duplicates across all loaded config
    /// files raise a <see cref="UserInputException"/>.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Exact MCP URL this entry binds to. Required - patterns belong in
    /// <see cref="ScanConfig.TargetPatterns"/>. Compared case-insensitively on scheme/host,
    /// case-sensitively on path (matches browser convention). Trailing slashes are ignored
    /// for comparison purposes.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>
    /// HTTP headers to merge into outbound requests against this MCP. Header NAMES are
    /// matched case-insensitively (HTTP convention); values are forwarded verbatim after
    /// environment-variable expansion.
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Where the merged headers apply. Default <see cref="TargetScope.All"/>.
    /// </summary>
    [JsonPropertyName("scope")]
    public TargetScope? Scope { get; init; }

    /// <summary>
    /// Optional auth profile name to bind to this target. Overlay precedence applies: a CLI
    /// <c>--profile</c> wins over this; this wins over <see cref="ScanConfig.TargetPatterns"/>.
    /// </summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    /// <summary>
    /// Transport override (<c>auto</c>, <c>streamable-http</c>, <c>sse</c>). Case-insensitive.
    /// </summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    /// <summary>
    /// Per-server handshake timeout in seconds. Overlay precedence applies (CLI &gt; target
    /// &gt; pattern &gt; default).
    /// </summary>
    [JsonPropertyName("timeoutSeconds")]
    public double? TimeoutSeconds { get; init; }

    /// <summary>
    /// Checks to disable for this target. Merged with the CLI <c>--disable</c> set and any
    /// matching <see cref="TargetPatternEntry.DisabledChecks"/> entries (union; once disabled,
    /// always disabled).
    /// </summary>
    [JsonPropertyName("disabledChecks")]
    public List<string>? DisabledChecks { get; init; }
}

/// <summary>
/// One declarative entry under the <c>targetPatterns</c> array in <c>McpLense.Config.json</c>.
/// Pattern entries apply to every MCP whose URL matches <see cref="Match"/>, providing
/// defaults that more specific <see cref="ScanTargetEntry"/> entries can override.
/// </summary>
public sealed class TargetPatternEntry
{
    /// <summary>
    /// URL-level glob expression. See <see cref="UrlGlob"/> for the supported grammar.
    /// </summary>
    [JsonPropertyName("match")]
    public string? Match { get; init; }

    /// <summary>
    /// Headers to merge into outbound requests against any MCP whose URL matches
    /// <see cref="Match"/>.
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Scope of how the merged headers apply. Default <see cref="TargetScope.All"/>.
    /// </summary>
    [JsonPropertyName("scope")]
    public TargetScope? Scope { get; init; }

    /// <summary>Auth profile to bind for every matched MCP. May be overridden per target.</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    /// <summary>Transport override; see <see cref="ScanTargetEntry.Transport"/>.</summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    /// <summary>Timeout override; see <see cref="ScanTargetEntry.TimeoutSeconds"/>.</summary>
    [JsonPropertyName("timeoutSeconds")]
    public double? TimeoutSeconds { get; init; }

    /// <summary>Disabled checks contributed by this pattern. Union-merged.</summary>
    [JsonPropertyName("disabledChecks")]
    public List<string>? DisabledChecks { get; init; }
}

/// <summary>
/// Result of resolving the per-target overlay for one URL. Carries the merged headers,
/// scope, optional profile / transport / timeout, the union of disabled-check ids, and a
/// human-readable summary of which entries matched (for the stderr "matched:" line).
/// </summary>
public sealed record TargetOverlay(
    IReadOnlyDictionary<string, string> Headers,
    TargetScope Scope,
    string? Profile,
    TransportPreference? Transport,
    TimeSpan? Timeout,
    IReadOnlyList<string> DisabledChecks,
    IReadOnlyList<string> MatchedPatterns,
    string? MatchedTargetName)
{
    public static readonly TargetOverlay Empty = new(
        Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Scope: TargetScope.All,
        Profile: null,
        Transport: null,
        Timeout: null,
        DisabledChecks: Array.Empty<string>(),
        MatchedPatterns: Array.Empty<string>(),
        MatchedTargetName: null);

    public bool HasAny
        => Headers.Count > 0
           || Profile is not null
           || Transport is not null
           || Timeout is not null
           || DisabledChecks.Count > 0
           || MatchedPatterns.Count > 0
           || MatchedTargetName is not null;
}
