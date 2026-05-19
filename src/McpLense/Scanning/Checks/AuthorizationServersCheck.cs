using System.Text.Json.Nodes;

namespace McpLense.Scanning.Checks;

/// <summary>
/// When the auth check classified the server as oauth-rfc9728, this check fetches every
/// advertised authorization server's RFC 8414 / OIDC discovery document. Disabled by
/// default - off-by-default protects air-gapped users from surprise outbound traffic to
/// login.microsoftonline.com &amp; friends.
/// </summary>
internal sealed class AuthorizationServersCheck : IScanCheck
{
    public string Id => "authorizationServers";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => false;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var authNode = context.GetPriorOutput("auth");
        if (authNode is not JsonObject authObj)
        {
            return CheckOutcome.Skipped;
        }

        var classification = authObj["classification"]?.GetValue<string>();
        if (!string.Equals(classification, AuthClassifications.OAuthRfc9728, StringComparison.Ordinal))
        {
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new AuthServersData([], null)), Error: null);
        }

        var issuers = ((authObj["details"] as JsonObject)?["authorizationServers"] as JsonArray)
            ?.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToArray() ?? Array.Empty<string>();

        if (issuers.Length == 0)
        {
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new AuthServersData([], null)), Error: null);
        }

        using var probe = new AuthorizationServerProbe();
        var entries = new List<ExpandedAuthorizationServerInfo>(issuers.Length);
        foreach (var issuer in issuers)
        {
            var basic = await probe.ProbeAsync(issuer, cancellationToken).ConfigureAwait(false);
            entries.Add(Expand(basic));
        }

        // DCR endpoint: pick the first AS that advertised a registration_endpoint.
        DcrInfo? dcr = null;
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.RegistrationEndpoint))
            {
                dcr = new DcrInfo(Endpoint: entry.RegistrationEndpoint, OpenRegistration: null);
                break;
            }
        }

        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new AuthServersData(entries, dcr)), Error: null);
    }

    /// <summary>
    /// Expands the basic <see cref="AuthorizationServerInfo"/> into the wider Tier 2 shape
    /// by reading additional fields from the verbatim <c>raw</c> document.
    /// </summary>
    private static ExpandedAuthorizationServerInfo Expand(AuthorizationServerInfo basic)
    {
        string? Str(string property)
            => basic.Raw is JsonObject obj && obj[property] is JsonValue v && v.TryGetValue<string>(out var s)
                ? s
                : null;

        IReadOnlyList<string> StrArr(string property)
        {
            if (basic.Raw is not JsonObject obj || obj[property] is not JsonArray arr)
            {
                return [];
            }

            return arr.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToArray();
        }

        bool? Bool(string property)
            => basic.Raw is JsonObject obj && obj[property] is JsonValue v && v.TryGetValue<bool>(out var b)
                ? b
                : null;

        return new ExpandedAuthorizationServerInfo(
            Issuer: basic.Issuer,
            Fetched: basic.Fetched,
            FetchError: basic.FetchError,
            AuthorizationEndpoint: basic.AuthorizationEndpoint,
            TokenEndpoint: basic.TokenEndpoint,
            RegistrationEndpoint: basic.RegistrationEndpoint,
            IntrospectionEndpoint: basic.IntrospectionEndpoint,
            RevocationEndpoint: basic.RevocationEndpoint,
            JwksUri: basic.JwksUri,
            UserinfoEndpoint: Str("userinfo_endpoint"),
            EndSessionEndpoint: Str("end_session_endpoint"),
            ScopesSupported: basic.ScopesSupported,
            ResponseTypesSupported: basic.ResponseTypesSupported,
            GrantTypesSupported: basic.GrantTypesSupported,
            TokenEndpointAuthMethodsSupported: basic.TokenEndpointAuthMethodsSupported,
            CodeChallengeMethodsSupported: basic.CodeChallengeMethodsSupported,
            ResourceParameterSupported: basic.ResourceParameterSupported,
            DpopSigningAlgValuesSupported: StrArr("dpop_signing_alg_values_supported"),
            RequirePushedAuthorizationRequests: Bool("require_pushed_authorization_requests"),
            PushedAuthorizationRequestEndpoint: Str("pushed_authorization_request_endpoint"),
            MtlsEndpointAliases: CheckSessionHelpers.SafeNode((basic.Raw as JsonObject)?["mtls_endpoint_aliases"]),
            IdTokenSigningAlgValuesSupported: StrArr("id_token_signing_alg_values_supported"),
            SubjectTypesSupported: StrArr("subject_types_supported"),
            RequestObjectSigningAlgValuesSupported: StrArr("request_object_signing_alg_values_supported"),
            RequestParameterSupported: Bool("request_parameter_supported"),
            RequestUriParameterSupported: Bool("request_uri_parameter_supported"),
            BackchannelLogoutSupported: Bool("backchannel_logout_supported"),
            BackchannelLogoutSessionSupported: Bool("backchannel_logout_session_supported"),
            AcrValuesSupported: StrArr("acr_values_supported"),
            PromptValuesSupported: StrArr("prompt_values_supported"),
            Raw: basic.Raw);
    }

    internal sealed record AuthServersData(
        IReadOnlyList<ExpandedAuthorizationServerInfo> Servers,
        DcrInfo? DcrFromResourceMetadata);

    internal sealed record ExpandedAuthorizationServerInfo(
        string Issuer,
        bool Fetched,
        string? FetchError,
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? RegistrationEndpoint,
        string? IntrospectionEndpoint,
        string? RevocationEndpoint,
        string? JwksUri,
        string? UserinfoEndpoint,
        string? EndSessionEndpoint,
        IReadOnlyList<string> ScopesSupported,
        IReadOnlyList<string> ResponseTypesSupported,
        IReadOnlyList<string> GrantTypesSupported,
        IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
        IReadOnlyList<string> CodeChallengeMethodsSupported,
        bool? ResourceParameterSupported,
        IReadOnlyList<string> DpopSigningAlgValuesSupported,
        bool? RequirePushedAuthorizationRequests,
        string? PushedAuthorizationRequestEndpoint,
        JsonNode? MtlsEndpointAliases,
        IReadOnlyList<string> IdTokenSigningAlgValuesSupported,
        IReadOnlyList<string> SubjectTypesSupported,
        IReadOnlyList<string> RequestObjectSigningAlgValuesSupported,
        bool? RequestParameterSupported,
        bool? RequestUriParameterSupported,
        bool? BackchannelLogoutSupported,
        bool? BackchannelLogoutSessionSupported,
        IReadOnlyList<string> AcrValuesSupported,
        IReadOnlyList<string> PromptValuesSupported,
        JsonNode? Raw);
}
