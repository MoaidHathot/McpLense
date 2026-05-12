namespace McpLense;

/// <summary>
/// Picks the right <see cref="AuthProfile"/> for an HTTP MCP target. Resolution rules:
/// <list type="number">
///   <item>Explicit <c>--profile &lt;name&gt;</c> wins (case-insensitive lookup; missing name errors).</item>
///   <item>Zero profiles loaded &rarr; error explaining how to set one up.</item>
///   <item>Exactly one profile loaded &rarr; use it. No probe, no cache check needed; the answer
///   is unambiguous and any extra HTTP round-trip would just add latency.</item>
///   <item>Multiple profiles loaded &rarr; probe the URL for RFC 9728 advertised scopes, narrow
///   the candidate set, then pick the unique profile that already has a cached account.</item>
///   <item>If the cache check still leaves multiple candidates (or zero), apply the precedence
///   tiebreaker (<see cref="EffectivePriority(AuthProfile)"/>): higher wins. Profiles can set an
///   explicit <c>priority</c> in JSON; otherwise the kind-based default applies
///   (azure-cli &gt; interactive-browser &gt; oauth &gt; bearer).</item>
///   <item>If the tiebreaker leaves multiple candidates at the same effective priority, error
///   with a disambiguation hint.</item>
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

        // Single-profile shortcut: nothing to disambiguate, skip the probe.
        if (profiles.Count == 1)
        {
            return profiles[0];
        }

        var probeResult = await _probe.ProbeAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        var candidates = NarrowByProbe(profiles, probeResult);

        // From the candidate set, find which profiles already have cached credentials.
        var cachedCandidates = new List<AuthProfile>();
        foreach (var candidate in candidates)
        {
            if (await _cacheInspector.HasCachedAccountAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                cachedCandidates.Add(candidate);
            }
        }

        // Pick from the cached set first; fall through to all candidates if none cached.
        var picked = PickByPrecedence(cachedCandidates) ?? PickByPrecedence(candidates);
        if (picked is not null)
        {
            return picked;
        }

        // The tiebreaker couldn't disambiguate - multiple candidates tied at the same effective
        // priority. Surface that as an actionable error.
        var tied = cachedCandidates.Count > 0 ? cachedCandidates : candidates;
        var topPriority = tied.Max(EffectivePriority);
        var tiedNames = tied.Where(p => EffectivePriority(p) == topPriority).Select(p => p.Name);
        var hint = cachedCandidates.Count > 0
            ? "Use '--profile <name>' to disambiguate."
            : $"Run 'mcplense login --profile <name>' first, or pass --profile to pick one of: {string.Join(", ", candidates.Select(p => p.Name))}.";

        var lead = cachedCandidates.Count > 0
            ? $"Multiple profiles already have cached credentials for {serverUrl}: {string.Join(", ", tiedNames)}."
            : $"No cached credentials match {serverUrl}; multiple profiles tied at priority {topPriority}: {string.Join(", ", tiedNames)}.";

        throw new UserInputException($"{lead} {hint}");
    }

    /// <summary>
    /// Default ranks by auth kind. Higher = preferred. Used when a profile doesn't override
    /// via the JSON <c>priority</c> field. The values are spaced (100, 200, 300, 400) so users
    /// can squeeze profiles in between by setting an explicit priority.
    /// </summary>
    /// <remarks>
    /// Rationale (high &rarr; low):
    /// <list type="bullet">
    ///   <item><c>AzureCli</c>: truly silent, inherits an existing <c>az login</c> session, no
    ///   browser ever. Best CI / SSH / headless story.</item>
    ///   <item><c>InteractiveBrowser</c>: silent when cached, MSAL is the most robust path for
    ///   Entra. Falls back to a browser pop on first run.</item>
    ///   <item><c>OAuth</c>: generic MCP-spec OAuth; slower discovery + potential DCR. Worse for
    ///   Entra targets specifically (Entra rejects RFC 7591 DCR).</item>
    ///   <item><c>Bearer</c>: static token; in practice rarely conflicts with the others
    ///   because the typical bearer use case (GitHub tokens, API keys) targets non-Entra hosts.</item>
    /// </list>
    /// </remarks>
    internal static int DefaultRankFor(AuthKind kind) => kind switch
    {
        AuthKind.AzureCli => 400,
        AuthKind.InteractiveBrowser => 300,
        AuthKind.OAuth => 200,
        AuthKind.Bearer => 100,
        _ => 0
    };

    /// <summary>
    /// Effective priority for sorting candidates. An explicit <see cref="AuthProfile.Priority"/>
    /// wins; otherwise we fall back to the kind-based default.
    /// </summary>
    internal static int EffectivePriority(AuthProfile profile)
        => profile.Priority ?? DefaultRankFor(profile.Auth.Kind);

    /// <summary>
    /// Returns the unique top-priority candidate, or null when zero or multiple share the top
    /// priority (the resolver surfaces the "tied" case as an error).
    /// </summary>
    private static AuthProfile? PickByPrecedence(IReadOnlyList<AuthProfile> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var topPriority = candidates.Max(EffectivePriority);
        var topTier = candidates.Where(p => EffectivePriority(p) == topPriority).ToList();
        return topTier.Count == 1 ? topTier[0] : null;
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
