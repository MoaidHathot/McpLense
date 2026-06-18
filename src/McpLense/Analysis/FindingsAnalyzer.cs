using McpLense.Analysis.Rules;
using McpLense.Scanning;

namespace McpLense.Analysis;

/// <summary>
/// The analysis layer: runs a set of <see cref="IFindingRule"/>s over a fact-only
/// <see cref="ScanReport"/> and produces a <see cref="FindingsReport"/>. It is a pure consumer of
/// the scan output - it never re-probes the network and never mutates the scan facts - which keeps
/// the "scan extracts facts, analysis classifies" separation the project is built around.
/// </summary>
public sealed class FindingsAnalyzer
{
    private readonly IReadOnlyList<IFindingRule> _rules;

    /// <summary>Builds an analyzer over the supplied rules, or the built-in set when null.</summary>
    public FindingsAnalyzer(IEnumerable<IFindingRule>? rules = null)
        => _rules = (rules ?? BuiltInRules).ToList();

    /// <summary>The built-in rule pack (codifies the security-classification recipes).</summary>
    public static IReadOnlyList<IFindingRule> BuiltInRules { get; } =
    [
        new OpenShapeInputRule(),
        new MissingDestructiveHintRule(),
        new AnonymousDestructiveRule(),
        new UnannouncedBearerRule(),
        new PromptInjectionRule(),
        new DescriptionUrlRule(),
        new ErrorLeakRule(),
        new MalformedHandlingRule(),
        new WeakCorsRule(),
        new TlsExpiryRule(),
        new MixedContentRule(),
        new TlsChainInvalidRule()
    ];

    /// <summary>
    /// Evaluates every enabled rule against every server in the report. Config (when supplied) can
    /// disable rules and override their severity; a rule that throws is isolated so one bad rule
    /// can't sink the whole analysis. Findings are sorted most-severe-first per server.
    /// </summary>
    public FindingsReport Analyze(ScanReport report, AnalysisConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        config ??= new AnalysisConfig();

        var servers = new List<ServerFindings>(report.Servers.Count);
        foreach (var server in report.Servers)
        {
            var findings = new List<Finding>();
            foreach (var rule in _rules)
            {
                if (!config.IsRuleEnabled(rule.Id, rule.DefaultEnabled))
                {
                    continue;
                }

                IReadOnlyList<Finding> ruleFindings;
                try
                {
                    ruleFindings = rule.Evaluate(server).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    McpLense.Diagnostics.McpLenseLog.Write($"analysis: rule '{rule.Id}' threw and was skipped: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                foreach (var finding in ruleFindings)
                {
                    var severity = config.SeverityFor(rule.Id, finding.Severity);
                    findings.Add(finding with { Severity = severity });
                }
            }

            findings.Sort((a, b) => b.Severity.CompareTo(a.Severity));
            servers.Add(new ServerFindings(server.Name, server.Target, findings));
        }

        return new FindingsReport(DateTimeOffset.UtcNow, servers);
    }
}
