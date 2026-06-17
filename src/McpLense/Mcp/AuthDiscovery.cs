namespace McpLense;

/// <summary>
/// The result of the discovery probe: either the raw signals, or a formatted error string when the
/// probe itself threw. The default <see cref="AuthProbe"/> swallows network errors and returns an
/// inconclusive result, so the error path is defensive (e.g. an injected test stub that throws).
/// </summary>
internal sealed record AuthProbeOutcome(AuthProbeResult? Result, string? ProbeError);

/// <summary>
/// Owns all interaction with the RFC 9728 <see cref="IAuthProbe"/>: the protected-resource-metadata
/// probe used to classify a server, and the scope substitution that lets a probe-aware profile pick
/// up server-advertised scopes. Extracted from <see cref="AuthScanner"/> so the probe is the only
/// dependency this layer knows about and the orchestrator/classifier never touch it directly.
/// </summary>
internal sealed class AuthDiscovery(IAuthProbe probe)
{
    private readonly IAuthProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <summary>
    /// Probes the server's protected-resource metadata, forwarding per-target headers (when
    /// scope=all) so a server that gates everything behind a custom header can still surface its
    /// RFC 9728 challenge - same-origin only. A genuine user-cancellation propagates; any other
    /// exception is captured as a ProbeError so the classifier can report Unknown instead of
    /// aborting the whole scan.
    /// </summary>
    internal async Task<AuthProbeOutcome> ProbeAsync(ResolvedServer server, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _probe.ProbeAsync(
                server.Url!,
                server.Headers.Count == 0 ? null : server.Headers,
                server.HeaderScope,
                cancellationToken).ConfigureAwait(false);
            return new AuthProbeOutcome(result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AuthProbeOutcome(null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reuses the runtime scope-substitution logic so a probe-aware profile picks up the same scopes
    /// 'inspect' would. Critical for Entra-style "&lt;audience&gt;/.default" profiles where the probe
    /// usually has a better match; <paramref name="defaultScopeFallback"/> covers AAD MCPs with no PRM.
    /// </summary>
    internal Task<ResolvedAuth> SubstituteScopesAsync(
        ResolvedAuth auth,
        Uri serverUrl,
        string? defaultScopeFallback,
        CancellationToken cancellationToken)
        => McpExecutor.MaybeSubstituteScopesFromProbeAsync(auth, serverUrl, _probe, cancellationToken, defaultScopeFallback: defaultScopeFallback);
}
