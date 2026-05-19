using System.Text.Json.Nodes;

namespace McpLense.Scanning;

/// <summary>
/// A self-contained probe that contributes one entry to a <see cref="ScanReport"/>. Each
/// implementation is responsible for inspecting one facet of the target (auth, TLS, tools,
/// behaviour) and returning a <see cref="CheckOutcome"/> with verbatim data and no
/// editorial labelling. The scan pipeline orchestrates them: respects <see cref="DependsOn"/>
/// for ordering, runs independent checks in parallel within a single server, and never
/// lets a check's exception escape the pipeline (caught and surfaced as
/// <see cref="CheckOutcome.Error"/>).
/// </summary>
/// <remarks>
/// Extension point: third-party tooling can implement this interface in their own assembly,
/// reference <c>McpLense</c>, and register the check via
/// <c>ScanPipelineBuilder.AddCheck&lt;T&gt;()</c> or
/// <c>IServiceCollection.AddScanCheck&lt;T&gt;()</c>. Built-in checks live under
/// <c>McpLense.Scanning.Checks</c> and follow the same pattern.
/// </remarks>
public interface IScanCheck
{
    /// <summary>
    /// Stable wire identifier (e.g. <c>"auth"</c>, <c>"tls"</c>, <c>"metrics"</c>) used as the
    /// key under <c>checks.&lt;id&gt;</c> in the JSON report AND in the config file's
    /// <c>scan.checks.&lt;id&gt;</c> section. Treat as a public contract once shipped.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Other check ids whose outputs this check needs to read from
    /// <see cref="ScanContext.PriorOutputs"/>. The pipeline runs checks in topological order
    /// over this graph; independent checks may run in parallel within a single server.
    /// </summary>
    IReadOnlyList<string> DependsOn { get; }

    /// <summary>
    /// Whether the check runs out of the box when the user doesn't override anything in
    /// config. Most defaults are <c>true</c> (fact-extraction checks); behavioural and
    /// outbound probes default to <c>false</c> so a default scan stays cheap and safe.
    /// </summary>
    bool IsEnabledByDefault { get; }

    /// <summary>
    /// Runs the check. Implementations MUST NOT throw past this method; catch and surface
    /// failures via <see cref="CheckOutcome.Error"/> so a single bad check doesn't sink the
    /// rest of the report.
    /// </summary>
    Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of one <see cref="IScanCheck.RunAsync"/> call. Three fields capture the three
/// independent facts: did the check run at all (was it enabled and did its dependencies
/// succeed), what data did it produce (the verbatim payload that becomes
/// <c>checks.&lt;id&gt;</c> in the report), and did it fail (free-form error message).
/// </summary>
/// <param name="Ran">True when the check executed; false when disabled or its dependencies were missing.</param>
/// <param name="Data">Verbatim data the check wants to record. Becomes <c>checks.&lt;id&gt;</c> in the report.</param>
/// <param name="Error">Free-form failure message when something went wrong; null on success.</param>
public sealed record CheckOutcome(bool Ran, JsonNode? Data, string? Error)
{
    /// <summary>Convenience: an outcome that did run successfully and returned no payload.</summary>
    public static CheckOutcome OkNoData { get; } = new(Ran: true, Data: null, Error: null);

    /// <summary>Convenience: an outcome that was skipped (not enabled / deps unavailable).</summary>
    public static CheckOutcome Skipped { get; } = new(Ran: false, Data: null, Error: null);
}
