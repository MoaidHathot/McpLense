namespace McpLense;

/// <summary>The outcome of classifying one server: the stable label, a human summary, and the raw signals.</summary>
internal sealed record AuthClassification(string Classification, string Summary, AuthScanDetails Details);

/// <summary>
/// Pure auth-model decision logic, extracted from <see cref="AuthScanner"/>. Takes already-gathered
/// signals (an <see cref="AuthProbeResult"/> and, when needed, a <see cref="HandshakeResult"/>) and
/// produces a classification - it performs no I/O of its own, so the whole decision tree is unit
/// testable without a network or credentials. The orchestrator (<see cref="AuthScanner"/>) owns the
/// probe + handshake calls and decides when a handshake is required (only when the probe didn't
/// surface an explicit challenge).
/// </summary>
internal static class AuthClassifier
{
    /// <summary>Projects the probe's raw signals into the report's details record (handshake fields stay null).</summary>
    internal static AuthScanDetails BuildBaseDetails(AuthProbeResult probe)
        => new(
            StatusCode: probe.StatusCode,
            ReasonPhrase: probe.ReasonPhrase,
            WwwAuthenticate: probe.WwwAuthenticate,
            ResourceMetadataUrl: probe.ResourceMetadataUrl,
            Resource: probe.Resource,
            Scopes: probe.Scopes,
            AuthorizationServers: probe.AuthorizationServers,
            DiagnosticHeaders: probe.DiagnosticHeaders);

    /// <summary>The probe attempt itself failed (the default <see cref="AuthProbe"/> swallows network
    /// errors and returns Inconclusive, so this is the defensive/stub-threw path).</summary>
    internal static AuthClassification FromProbeError(string probeError)
        => new(
            AuthClassifications.Unknown,
            "Probe threw an exception; classification unknown.",
            new AuthScanDetails(ProbeError: probeError));

    /// <summary>
    /// Branch 1: the probe surfaced an explicit auth challenge (401 / WWW-Authenticate), so we can
    /// classify without an MCP handshake. Returns <c>null</c> when the probe carried no auth signal
    /// and the caller must fall back to the unauthenticated handshake (<see cref="ClassifyAfterHandshake"/>).
    /// </summary>
    internal static AuthClassification? ClassifyFromProbe(AuthProbeResult probe, AuthScanDetails baseDetails)
    {
        if (!probe.RequiresAuth)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(probe.ResourceMetadataUrl))
        {
            var summary = (probe.Scopes is { Count: > 0 })
                ? $"OAuth via RFC 9728 - {probe.Scopes.Count} scope(s) advertised at {probe.ResourceMetadataUrl}."
                : $"OAuth via RFC 9728 - metadata pointer at {probe.ResourceMetadataUrl} (no scopes_supported in the document).";
            return new(AuthClassifications.OAuthRfc9728, summary, baseDetails);
        }

        if (HasBearerChallenge(probe.WwwAuthenticate))
        {
            return new(
                AuthClassifications.OAuthBearerUnannounced,
                "Server demands Bearer auth but does not advertise RFC 9728 protected-resource metadata.",
                baseDetails);
        }

        var headerHint = string.IsNullOrEmpty(probe.WwwAuthenticate)
            ? "no WWW-Authenticate header"
            : $"scheme '{probe.WwwAuthenticate}'";
        return new(
            AuthClassifications.AuthRequiredUnspecified,
            $"Server demands authentication but uses an unrecognised challenge ({headerHint}).",
            baseDetails);
    }

    /// <summary>
    /// Branch 2: the probe carried no auth challenge, so the unauthenticated MCP <c>initialize</c>
    /// handshake is the authoritative test. Success means genuinely anonymous; an auth-looking
    /// failure downgrades to auth-required; any other failure stays Unknown with a tailored summary.
    /// </summary>
    internal static AuthClassification ClassifyAfterHandshake(AuthProbeResult probe, AuthScanDetails baseDetails, HandshakeResult handshake)
    {
        var detailsWithHandshake = baseDetails with
        {
            AnonymousHandshakeSucceeded = handshake.Success,
            AnonymousHandshakeError = handshake.Error
        };

        if (handshake.Success)
        {
            return new(
                AuthClassifications.Anonymous,
                "Server accepts unauthenticated MCP sessions.",
                detailsWithHandshake);
        }

        if (LooksLikeAuthError(handshake.Error))
        {
            return new(
                AuthClassifications.AuthRequiredUnspecified,
                "Server rejected the unauthenticated MCP handshake with what looks like an auth error (the GET probe did not surface this challenge).",
                detailsWithHandshake);
        }

        var summaryWhenUnknown = probe.Inconclusive
            ? (probe.StatusCode is { } status
                ? $"Probe returned {status} without an auth challenge and the unauthenticated MCP handshake also failed; classification inconclusive."
                : "Probe could not reach the server and the unauthenticated MCP handshake also failed.")
            : "Probe surfaced no auth signal and the unauthenticated MCP handshake failed for non-auth reasons.";

        return new(AuthClassifications.Unknown, summaryWhenUnknown, detailsWithHandshake);
    }

    /// <summary>
    /// Coarse, consumer-facing reachability label derived from the raw probe signals + the final
    /// classification. See <see cref="ServerAccessibility"/> for the stable wire values. Centralising
    /// the derivation means every fleet consumer sees the same answer instead of re-implementing the
    /// (status-code, www-authenticate, handshake) decision tree.
    /// </summary>
    internal static string DeriveServerStatus(string classification, AuthScanDetails details)
    {
        switch (classification)
        {
            case AuthClassifications.Stdio:
            case AuthClassifications.Anonymous:
                return ServerAccessibility.Accessible;

            case AuthClassifications.OAuthRfc9728:
            case AuthClassifications.OAuthBearerUnannounced:
            case AuthClassifications.AuthRequiredUnspecified:
                return ServerAccessibility.RequiresAuth;
        }

        if (details.StatusCode is { } status)
        {
            if (status is 404 or 410)
            {
                return ServerAccessibility.NotFound;
            }
        }
        else if (!string.IsNullOrEmpty(details.ProbeError))
        {
            return ServerAccessibility.Unreachable;
        }

        return ServerAccessibility.Unknown;
    }

    /// <summary>
    /// RFC numbers implicated by the classification. Empty for classifications that don't map to any
    /// specific RFC (anonymous, stdio, non-Bearer challenges, unknown).
    /// </summary>
    internal static IReadOnlyList<string> DeriveRfcs(string classification) => classification switch
    {
        AuthClassifications.OAuthRfc9728 => new[] { "RFC 9728", "RFC 6750", "RFC 8414" },
        AuthClassifications.OAuthBearerUnannounced => new[] { "RFC 6750" },
        _ => Array.Empty<string>()
    };

    internal static string FormatCapabilitySummary(HandshakeResult handshake)
    {
        var parts = new List<string>();
        if (handshake.ToolCount is { } t)
        {
            parts.Add($"{t} tool(s)");
        }

        if (handshake.ResourceCount is { } r)
        {
            parts.Add($"{r} resource(s)");
        }

        if (handshake.PromptCount is { } p)
        {
            parts.Add($"{p} prompt(s)");
        }

        return parts.Count == 0
            ? "Handshake succeeded; no capability lists available."
            : "Handshake succeeded: " + string.Join(", ", parts) + ".";
    }

    internal static string AuthKindToString(AuthKind kind) => kind switch
    {
        AuthKind.Bearer => "bearer",
        AuthKind.OAuth => "oauth",
        AuthKind.InteractiveBrowser => "interactive-browser",
        AuthKind.AzureCli => "azure-cli",
        AuthKind.None => "none",
        _ => kind.ToString().ToLowerInvariant()
    };

    private static bool HasBearerChallenge(string? wwwAuthenticate)
    {
        if (string.IsNullOrEmpty(wwwAuthenticate))
        {
            return false;
        }

        // The header is a comma-separated list of challenges; "Bearer" anywhere as a scheme counts.
        // Be lenient about case to match RFC 7235.
        foreach (var part in wwwAuthenticate.Split(','))
        {
            var trimmed = part.TrimStart();
            if (trimmed.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                // Make sure it's the scheme token, not "Bearertoken" - the scheme is followed by
                // whitespace, end-of-string, or a parameter list.
                if (trimmed.Length == "Bearer".Length
                    || char.IsWhiteSpace(trimmed["Bearer".Length])
                    || trimmed["Bearer".Length] == ',')
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Heuristic match for "this exception message describes a 401/403 response from the MCP server".
    /// Used to downgrade an anonymous-probe verdict to "auth required" when the actual MCP handshake
    /// says otherwise. False positives are tolerable; false negatives leave a real auth-required
    /// server labelled "Unknown".
    /// </summary>
    private static bool LooksLikeAuthError(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return false;
        }

        return error.Contains("401", StringComparison.Ordinal)
               || error.Contains("403", StringComparison.Ordinal)
               || error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
               || error.Contains("authentication", StringComparison.OrdinalIgnoreCase);
    }
}
