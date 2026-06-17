using McpLense.Scanning;

namespace McpLense.Analysis;

/// <summary>
/// A single security/quality rule that turns the fact-only output of a scanned server into zero or
/// more <see cref="Finding"/>s. Rules are pure - they read <see cref="ServerScanResult.Checks"/> and
/// emit findings, performing no I/O - so the whole analysis layer is deterministic and unit-testable
/// from a hand-built <see cref="ServerScanResult"/>.
/// </summary>
/// <remarks>
/// Extension point: external assemblies can implement this and contribute rules to a
/// <see cref="FindingsAnalyzer"/>, mirroring the <see cref="IScanCheck"/> plugin model. Built-in
/// rules live under <c>McpLense.Analysis.Rules</c>.
/// </remarks>
public interface IFindingRule
{
    /// <summary>Stable wire id, used as the finding's <c>ruleId</c> and the config key
    /// (<c>analysis.rules.&lt;id&gt;</c>). Treat as a public contract once shipped.</summary>
    string Id { get; }

    /// <summary>Whether the rule runs when the user hasn't enabled/disabled it in config.</summary>
    bool DefaultEnabled { get; }

    /// <summary>
    /// Evaluate the rule against one server's fact-only check outputs. Implementations MUST be
    /// defensive about missing/mis-shaped check data (a check may not have run) and simply yield
    /// nothing rather than throwing.
    /// </summary>
    IEnumerable<Finding> Evaluate(ServerScanResult facts);
}
