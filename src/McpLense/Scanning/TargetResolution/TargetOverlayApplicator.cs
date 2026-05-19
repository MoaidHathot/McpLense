using System.Text;

namespace McpLense.Scanning.TargetResolution;

/// <summary>
/// Applies a <see cref="ScanConfig"/>'s <c>targets[]</c> / <c>targetPatterns[]</c> overlay
/// to a resolved server list. Shared by <see cref="ScanCommandDispatcher"/> (the
/// scan-pipeline entry point) and <see cref="McpExecutor"/> (every other command:
/// <c>inspect</c>, <c>tools</c>, <c>resources</c>, <c>prompts</c>, <c>call</c>,
/// <c>read</c>, <c>prompt</c>, <c>fetch-resource</c>, <c>auth-scan</c>, <c>observe</c>).
/// </summary>
/// <remarks>
/// Producing the per-server overlay here (and not in the command dispatch) means:
///   - The "matched: ..." stderr line fires uniformly for every command, so users can
///     verify which headers / profile / transport are about to ride along.
///   - The per-target <c>scope</c> / <c>headers</c> / <c>transport</c> /
///     <c>timeoutSeconds</c> / <c>disabledChecks</c> binding applies regardless of
///     which command the user invoked, fixing the original gap where headers only
///     reached the scan pipeline.
/// </remarks>
internal static class TargetOverlayApplicator
{
    /// <summary>
    /// Threads a <see cref="ScanConfig"/> overlay through each resolved server. HTTP
    /// servers (with a URL) get the merged headers / scope / transport / timeout /
    /// disabled-checks; stdio servers are returned unchanged. The optional
    /// <paramref name="cliHeaders"/> wins over config (per-key) per the resolver's
    /// precedence rules. When <paramref name="quiet"/> is false a one-line "matched:"
    /// summary fires to stderr for every server whose overlay produced any effect.
    /// </summary>
    public static IReadOnlyList<ResolvedServer> Apply(
        IReadOnlyList<ResolvedServer> servers,
        ScanConfig scanConfig,
        TargetOptions target,
        IReadOnlySet<string>? cliDisables,
        bool quiet,
        bool verbose = false,
        TextWriter? stderr = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(scanConfig);
        ArgumentNullException.ThrowIfNull(target);

        stderr ??= Console.Error;

        var overlaid = new List<ResolvedServer>(servers.Count);
        for (var i = 0; i < servers.Count; i++)
        {
            var server = servers[i];
            if (server.Url is null)
            {
                // Stdio targets have no HTTP overlay surface.
                overlaid.Add(server);
                continue;
            }

            var overlay = TargetOverlayResolver.Resolve(
                scanConfig,
                server.Url,
                namedReference: target.NamedReference,
                cliHeaders: target.Headers,
                cliProfile: null, // profile handling lives in AuthOverrides today
                cliTransport: target.Transport,
                cliTimeout: null, // global handshake timeout still wins
                cliDisables: cliDisables);

            if (!quiet && overlay.HasAny && (overlay.MatchedPatterns.Count > 0 || overlay.MatchedTargetName is not null))
            {
                EmitMatchedLine(stderr, overlay, server.Url, verbose);
            }

            // Merge overlay headers on top of any headers already on the server (TargetResolver
            // may have populated them from the URL flow). Pattern-then-target-then-CLI per-key
            // precedence was already resolved inside TargetOverlayResolver; here we just
            // overlay the resolved set onto the server.
            var mergedHeaders = new Dictionary<string, string>(server.Headers, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in overlay.Headers)
            {
                mergedHeaders[k] = v;
            }

            overlaid.Add(server with
            {
                Headers = mergedHeaders,
                HeaderScope = overlay.Scope,
                Transport = overlay.Transport ?? server.Transport,
                DisabledChecks = overlay.DisabledChecks.Count == 0 ? server.DisabledChecks : overlay.DisabledChecks,
                HandshakeTimeout = overlay.Timeout
            });
        }

        return overlaid;
    }

    /// <summary>
    /// Resolves a <c>@name</c> positional reference to a concrete URL using the loaded
    /// <see cref="ScanConfig.Targets"/> list. Returns the original <paramref name="target"/>
    /// unchanged when no <c>NamedReference</c> was supplied OR a URL was already supplied
    /// alongside it (CLI parser already rejects that combination - the check here is
    /// defensive). Throws when the reference name doesn't resolve.
    /// </summary>
    public static TargetOptions ResolveNamedReference(TargetOptions target, ScanConfig scanConfig)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(scanConfig);

        if (string.IsNullOrEmpty(target.NamedReference) || target.Url is not null)
        {
            return target;
        }

        var resolved = TargetOverlayResolver.ResolveNamedTargetUrl(scanConfig, target.NamedReference!);
        if (string.IsNullOrEmpty(resolved))
        {
            throw new UserInputException(
                $"Target reference '@{target.NamedReference}' was not found. " +
                $"Add a `targets` entry with `name: \"{target.NamedReference}\"` to your McpLense.Config.json file " +
                "or pass a positional URL instead.");
        }

        if (!Uri.TryCreate(resolved, UriKind.Absolute, out var resolvedUri))
        {
            throw new UserInputException(
                $"Target '@{target.NamedReference}' has an invalid `url`: '{resolved}'.");
        }

        return target with { Url = resolvedUri };
    }

    private static void EmitMatchedLine(TextWriter stderr, TargetOverlay overlay, Uri serverUrl, bool verbose)
    {
        var patternBit = overlay.MatchedPatterns.Count > 0
            ? $"patterns={overlay.MatchedPatterns.Count}"
            : "patterns=0";
        var targetBit = overlay.MatchedTargetName is null ? "target=-" : $"target={overlay.MatchedTargetName}";

        // The summary line is intentionally short and grep-friendly so a fleet-scan stderr
        // remains tractable. The verbose extension prints each header's name + value so the
        // user can verify the overlay reached the server with the values they expected.
        // Values for sensitive header names (Authorization, Cookie, api-key style) are
        // redacted to length-only so they don't leak into terminal scrollback / log capture.
        var summary = $"matched: {patternBit} {targetBit} -> {overlay.Headers.Count} headers, scope={overlay.Scope.ToString().ToLowerInvariant()}";
        stderr.WriteLine(summary);

        if (verbose && overlay.Headers.Count > 0)
        {
            stderr.WriteLine($"matched headers for {serverUrl}:");
            foreach (var (name, value) in overlay.Headers)
            {
                var rendered = IsSensitiveHeader(name)
                    ? $"<redacted, length={value?.Length ?? 0}>"
                    : value ?? string.Empty;
                stderr.WriteLine($"  {name}: {rendered}");
            }

            if (overlay.MatchedPatterns.Count > 0)
            {
                stderr.WriteLine($"matched pattern(s): {string.Join("; ", overlay.MatchedPatterns)}");
            }
        }
    }

    /// <summary>
    /// Names of headers whose VALUES we redact in stderr logging because they typically
    /// carry secrets. Per-target identifier headers (org / project / repository / tenant /
    /// etc.) are printed verbatim - users explicitly placed those in the overlay and need
    /// to verify they reached the server. Bearer tokens / cookies / API keys are different
    /// in kind and stay redacted regardless of where in the overlay they originated.
    /// </summary>
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "api-key",
        "x-auth-token",
        "x-access-token"
    };

    private static bool IsSensitiveHeader(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (SensitiveHeaderNames.Contains(name))
        {
            return true;
        }

        // Catch-all for header names that look secret-shaped without being in the explicit
        // allowlist: `*-token`, `*-secret`, `*-password`. Case-insensitive.
        if (name.EndsWith("-token", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-secret", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apikey", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
