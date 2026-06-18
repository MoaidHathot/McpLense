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
    Analyze,
    Explain,
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
    Dumpify,
    /// <summary>
    /// SARIF 2.1.0 (Static Analysis Results Interchange Format) for the findings layer
    /// (<c>analyze</c> / <c>scan --findings</c>). Lets findings flow into GitHub code scanning and
    /// other SARIF-aware security tooling. Non-findings payloads fall back to JSON.
    /// </summary>
    Sarif,
    /// <summary>Markdown - a shareable, readable write-up. Best for <c>explain</c> / <c>inspect</c> /
    /// findings; other payloads fall back to a fenced text block.</summary>
    Markdown
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
    string? DefaultScope = null,
    /// <summary>
    /// When true, <c>call</c> / <c>read</c> / <c>prompt</c> prompt the user for arguments
    /// interactively (reading the target's tool input-schema / prompt arguments / URI-template
    /// variables) instead of taking them from <c>--args</c>. Declared defaults are pre-filled
    /// and accepted with Enter. CLI-only flag; ignored by library hosts that build arguments
    /// directly.
    /// </summary>
    bool Interactive = false,
    /// <summary>
    /// When true, the live MCP client keeps the standalone server-&gt;client GET event-stream open so
    /// server-initiated traffic (sampling / elicitation / roots / notifications) can arrive outside a
    /// request. Suppressed by default because some Streamable-HTTP servers drop the POST session when
    /// a parallel GET stream is opened; the <c>--server-stream</c> flag opts back in. CLI-only knob.
    /// </summary>
    bool ServerStream = false,
    /// <summary>
    /// <c>scan</c> only: when true, also run the analysis (findings) layer and emit facts + findings
    /// together. <c>analyze</c> always runs findings regardless of this flag.
    /// </summary>
    bool Findings = false,
    /// <summary>
    /// CI gate for <c>analyze</c> / <c>scan --findings</c>: a severity name (info/low/medium/high/
    /// critical). When any finding meets or exceeds it the process exits non-zero. Overrides
    /// <c>analysis.failOn</c> from config; null means "use config (or never gate)".
    /// </summary>
    string? FailOn = null,
    /// <summary>
    /// <c>analyze --approve &lt;file&gt;</c>: write an approval snapshot (per-item hashes) of the
    /// current server surface to this path - the trust anchor for later rug-pull detection.
    /// </summary>
    string? ApprovePath = null,
    /// <summary>
    /// <c>analyze --since &lt;file&gt;</c>: compare the current scan against this approval snapshot and
    /// emit rug-pull findings for any tool/prompt/resource that changed since it was approved.
    /// </summary>
    string? SincePath = null,
    /// <summary>
    /// <c>call &lt;tool&gt; --example</c>: instead of invoking, print a ready-to-edit example
    /// <c>--args</c> JSON generated from the tool's input schema (a learning/first-call aid).
    /// </summary>
    bool Example = false);

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

