namespace McpLense;

/// <summary>
/// Picks the right <see cref="AuthProfile"/> for an HTTP MCP target. Resolution rules:
/// <list type="number">
///   <item>Explicit <c>--profile &lt;name&gt;</c> wins (case-insensitive lookup; missing name errors).</item>
///   <item>Zero profiles loaded &rarr; error explaining how to set one up.</item>
///   <item>Exactly one profile loaded &rarr; use it. No probe, no cache check needed: the answer
///   is unambiguous and any extra HTTP round-trip would just add latency (and surface as a
///   misleading symptom when the server is slow or flaky on unauthenticated requests, e.g.
///   Agent365 returning 502/timeouts to HEAD probes).</item>
///   <item>Multiple profiles loaded &rarr; probe the URL for RFC 9728 advertised scopes, narrow
///   the candidate set, then pick the unique profile that already has a cached account. Errors
///   on ambiguity / no match with an actionable hint.</item>
/// </list>
/// </summary>
internal sealed class AuthProfileResolver
{
    private readonly IAuthProbe _probe;
    private readonly IMsalCacheInspector _cacheInspector;

    public AuthProfileResolver(IAuthProbe probe, IMsalCacheInspector cacheInspector)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _cacheInspector = cacheInspector ?? throw new ArgumentNullException(nameof(cacheInspector));
    }

    /// <summary>
    /// Returns the resolved profile, or null when no auth applies (caller should leave the
    /// server's <c>Auth</c> at <c>null</c>).
    /// </summary>
    /// <param name="serverUrl">Target server URL.</param>
    /// <param name="profiles">All loaded profiles.</param>
    /// <param name="requestedProfile">Explicit <c>--profile</c> value, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="UserInputException">Raised on ambiguous matches, no candidate, etc.</exception>
    public async Task<AuthProfile?> ResolveAsync(
        Uri serverUrl,
        IReadOnlyList<AuthProfile> profiles,
        string? requestedProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentNullException.ThrowIfNull(profiles);

        if (!string.IsNullOrEmpty(requestedProfile))
        {
            var match = profiles.FirstOrDefault(p => string.Equals(p.Name, requestedProfile, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var available = profiles.Count == 0
                    ? "(none loaded)"
                    : string.Join(", ", profiles.Select(p => p.Name));
                throw new UserInputException(
                    $"--profile '{requestedProfile}' was not found. Loaded profiles: {available}.");
            }

            return match;
        }

        if (profiles.Count == 0)
        {
            throw new UserInputException(
                $"No auth profiles are loaded. The MCP server at {serverUrl} appears to need authentication. " +
                $"Create a profile in '$XDG_CONFIG_HOME/McpLense/{DefaultConfigPaths.ProfilesFileName}', " +
                "pass one via --profiles <path>, or set --no-auth to bypass.");
        }

        // Single-profile shortcut: there's nothing to disambiguate, so don't pay for an HTTP
        // round-trip just to "confirm" what we already know. Some servers (Agent365, internal
        // Microsoft endpoints) are slow or flaky on unauthenticated HEAD/GET probes, and the
        // probe-on-every-call was producing 30+ second waits and double timeouts before. The
        // runtime path will fail authoritatively if this profile turns out not to fit.
        if (profiles.Count == 1)
        {
            return profiles[0];
        }

        var probeResult = await _probe.ProbeAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        var candidates = NarrowByProbe(profiles, probeResult);

        // From the candidate set, pick the unique profile that already has a cached account.
        var cachedCandidates = new List<AuthProfile>();
        foreach (var candidate in candidates)
        {
            if (await _cacheInspector.HasCachedAccountAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                cachedCandidates.Add(candidate);
            }
        }

        if (cachedCandidates.Count == 1)
        {
            return cachedCandidates[0];
        }

        if (cachedCandidates.Count > 1)
        {
            throw new UserInputException(
                $"Multiple profiles already have cached credentials for {serverUrl}: " +
                $"{string.Join(", ", cachedCandidates.Select(p => p.Name))}. " +
                "Use '--profile <name>' to disambiguate.");
        }

        // Zero cached candidates. If there is exactly one candidate (cached or not), use it -
        // the runtime will trigger interactive auth on first request.
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        throw new UserInputException(
            $"No cached credentials match {serverUrl}. " +
            $"Run 'mcplense login --profile <name>' first, " +
            $"or pass --profile to pick one of: {string.Join(", ", candidates.Select(p => p.Name))}.");
    }

    /// <summary>
    /// Returns every profile (in load order) when the probe surfaced no useful metadata.
    /// Otherwise filters by scope overlap (case-insensitive). Authorization-server narrowing is
    /// deferred to a future revision since matching tenant URIs requires Entra-specific parsing.
    /// </summary>
    private static IReadOnlyList<AuthProfile> NarrowByProbe(IReadOnlyList<AuthProfile> profiles, AuthProbeResult probeResult)
    {
        if (probeResult.Scopes is null || probeResult.Scopes.Count == 0)
        {
            return profiles;
        }

        var advertised = new HashSet<string>(probeResult.Scopes, StringComparer.OrdinalIgnoreCase);
        var narrowed = profiles
            .Where(p => p.Auth.Scopes is not null && p.Auth.Scopes.Any(scope => advertised.Contains(scope)))
            .ToList();

        // If nothing matches, fall back to the full set so the caller still has something to
        // work with (we don't want a stale advertised-scope list to block all profiles).
        return narrowed.Count > 0 ? narrowed : profiles;
    }
}
