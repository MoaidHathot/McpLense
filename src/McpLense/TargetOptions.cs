using System.Text.Json.Nodes;

namespace McpLense;

/// <summary>
/// HTTP transport preference for MCP connections. Maps to <c>ModelContextProtocol</c>'s
/// transport-mode enum.
/// </summary>
public enum TransportPreference
{
    Auto,
    StreamableHttp,
    Sse
}

/// <summary>
/// Logical command the executor needs to dispatch on. CLI parses arguments into this enum;
/// library hosts construct a <see cref="ParsedCommand"/> directly to drive the executor
/// without CLI parsing.
/// </summary>
internal enum AppCommand
{
    Help,
    Version,
    Tui,
    Inspect,
    Tools,
    Resources,
    Prompts,
    Call,
    Read,
    Prompt,
    Login,
    Logout,
    Scan,
    AuthScan,
    Observe,
    FetchResource,
    Diff,
    Schema
}

/// <summary>Output format hint passed from the CLI down to the renderer.</summary>
internal enum OutputFormat
{
    Text,
    Json,
    /// <summary>
    /// JSON Lines (NDJSON): one self-contained JSON document per output line. For
    /// <c>scan</c> reports the layout is <c>{"kind":"header",...}</c> + one
    /// <c>{"kind":"server",...}</c> per scanned server + <c>{"kind":"trailer",...}</c>.
    /// Designed for fleet-scale consumers that want to stream-read without buffering the
    /// whole report.
    /// </summary>
    Jsonl,
    Dumpify
}

/// <summary>
/// Fully-parsed command intent ready for the executor / pipeline. Built either from CLI
/// arguments via <c>CommandLineParser</c> in the CLI assembly or programmatically by an
/// embedding host that wants to drive the same code paths without parsing strings.
/// </summary>
internal sealed record ParsedCommand(
    AppCommand Command,
    string? Subject,
    JsonObject? Arguments,
    OutputFormat Format,
    TimeSpan Timeout,
    TargetOptions Target,
    bool ProgressEnabled,
    string? BaselinePath = null,
    string? DiffBaselinePath = null,
    IReadOnlyList<string>? CheckEnables = null,
    IReadOnlyList<string>? CheckDisables = null,
    int? ParallelServers = null,
    bool Quiet = false,
    bool Verbose = false,
    IReadOnlyList<string>? ScanPlugins = null,
    IReadOnlyList<string>? TargetsFromPaths = null,
    bool HttpOnly = false,
    string? DefaultScope = null);

/// <summary>
/// Resolved target description used to drive scans and other read-only operations. Public so
/// embedding hosts can construct one programmatically without going through CLI parsing.
/// </summary>
public sealed record TargetOptions(
    IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ProfilePaths,
    string? DisplayName,
    Uri? Url,
    TransportPreference Transport,
    IReadOnlyDictionary<string, string> Headers,
    string? Command,
    IReadOnlyList<string> CommandArguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    AuthOverrides AuthOverrides,
    string? NamedReference = null,
    /// <summary>
    /// Paths to plain-text files where each non-blank, non-comment line is either an absolute
    /// http(s) URL or an <c>@name</c> reference to a target in the loaded config. Lets fleet
    /// consumers hand McpLense the full target list so it owns the parallelism and connection
    /// pooling instead of forking one CLI process per server.
    /// </summary>
    IReadOnlyList<string>? TargetsFromPaths = null,
    /// <summary>
    /// When true, stdio targets resolved from <c>--config</c> are filtered out before the scan
    /// runs. Useful for fleet-wide HTTP scans where the same config also defines local stdio
    /// MCPs that the consumer doesn't care about.
    /// </summary>
    bool HttpOnly = false,
    /// <summary>
    /// Default OAuth scope used by profiles only when (a) the profile didn't pin a scope and
    /// (b) the RFC 9728 protected-resource metadata didn't advertise one. Designed for Entra
    /// / AAD-backed MCPs that don't speak PRM yet still need a <c>&lt;audience&gt;/.default</c>
    /// scope on the token request.
    /// </summary>
    string? DefaultScope = null);

/// <summary>
/// Raised when the caller supplied invalid input - bad URL, missing required option, etc.
/// The CLI catches this at the top level and prints help; embedding hosts can catch and
/// surface it however they like.
/// </summary>
public sealed class UserInputException : Exception
{
    public UserInputException(string message) : base(message) { }
}

