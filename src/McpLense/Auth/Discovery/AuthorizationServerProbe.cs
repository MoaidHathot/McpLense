using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpLense;

/// <summary>
/// Fetches an authorization server's OAuth 2.0 metadata document (RFC 8414) and surfaces the
/// security-relevant fields. The audit only invokes this probe when
/// <c>--check-authorization-servers</c> is set, so users running in air-gapped / restricted
/// environments don't get surprised by outbound requests to <c>login.microsoftonline.com</c>
/// or wherever else the protected-resource metadata pointed.
/// </summary>
internal interface IAuthorizationServerProbe
{
    Task<AuthorizationServerInfo> ProbeAsync(string issuer, CancellationToken cancellationToken);
}

internal sealed class AuthorizationServerProbe : IAuthorizationServerProbe, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    // RFC 8414 §3 specifies the metadata endpoint as
    //   {issuer}/.well-known/oauth-authorization-server
    // OIDC discovery uses
    //   {issuer}/.well-known/openid-configuration
    // Many Entra and Google issuers only serve the OIDC variant; we try both, OAuth first
    // because the field set is closer to what the audit cares about.
    private static readonly string[] WellKnownSuffixes =
    [
        ".well-known/oauth-authorization-server",
        ".well-known/openid-configuration"
    ];

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public AuthorizationServerProbe()
        : this(httpClient: null)
    {
    }

    /// <summary>
    /// Production overload: when an <see cref="IHttpClientFactory"/> is available (e.g.
    /// the CLI's <c>AddMcpLense</c> wires one in), reuse the shared <c>mcplense-probe</c>
    /// named client so sockets are pooled across every probe + check in the run.
    /// </summary>
    public AuthorizationServerProbe(IHttpClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _ownsHttpClient = false;
        _httpClient = factory.CreateClient(McpLense.Scanning.McpLenseServiceCollectionExtensions.ProbeHttpClientName);
    }

    /// <summary>For tests: inject a fake <see cref="HttpClient"/> backed by a test handler.</summary>
    internal AuthorizationServerProbe(HttpClient? httpClient)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler(), disposeHandler: true)
        {
            Timeout = DefaultTimeout
        };
    }

    public async Task<AuthorizationServerInfo> ProbeAsync(string issuer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            || (issuerUri.Scheme != Uri.UriSchemeHttp && issuerUri.Scheme != Uri.UriSchemeHttps))
        {
            return Empty(issuer, $"Issuer '{issuer}' is not an absolute http(s) URL.");
        }

        // The issuer URL must NOT have a trailing slash for path-joining per RFC 8414, but
        // many servers tolerate either form. Normalise both and try each well-known suffix.
        var trimmedIssuer = issuer.TrimEnd('/');

        Exception? lastFailure = null;
        foreach (var suffix in WellKnownSuffixes)
        {
            var url = $"{trimmedIssuer}/{suffix}";
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastFailure = new HttpRequestException($"GET {url} returned {(int)response.StatusCode}");
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Parse(issuer, body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
        }

        return Empty(
            issuer,
            lastFailure is null
                ? "No authorization-server metadata endpoint responded."
                : $"{lastFailure.GetType().Name}: {lastFailure.Message}");
    }

    private static AuthorizationServerInfo Parse(string issuer, string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            return Empty(issuer, $"Failed to parse authorization-server metadata JSON: {ex.Message}");
        }

        if (root is not JsonObject obj)
        {
            return Empty(issuer, "Authorization-server metadata is not a JSON object.");
        }

        // We surface the structurally interesting fields verbatim. The full document goes into
        // Raw so consumers can inspect anything else (issuer-specific extensions, vendor flags,
        // etc.) - we don't pretend to know in advance what they'll care about.
        string? Str(string property)
            => obj[property] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        IReadOnlyList<string> StrArr(string property)
        {
            if (obj[property] is not JsonArray array)
            {
                return [];
            }

            var values = new List<string>(array.Count);
            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    values.Add(s);
                }
            }

            return values;
        }

        // RFC 8707 "Resource Indicators" capability is advertised in two ways depending on the
        // server: some publish "resource_parameter_supported": true, others advertise it via
        // "authorization_response_iss_parameter_supported" or vendor-specific keys. We surface
        // the explicit boolean when present and leave nullable otherwise (no inference).
        bool? resourceParameter = obj["resource_parameter_supported"] is JsonValue rpv
            && rpv.TryGetValue<bool>(out var rp)
            ? rp
            : null;

        return new AuthorizationServerInfo(
            Issuer: issuer,
            Fetched: true,
            FetchError: null,
            AuthorizationEndpoint: Str("authorization_endpoint"),
            TokenEndpoint: Str("token_endpoint"),
            RegistrationEndpoint: Str("registration_endpoint"),
            IntrospectionEndpoint: Str("introspection_endpoint"),
            RevocationEndpoint: Str("revocation_endpoint"),
            JwksUri: Str("jwks_uri"),
            ScopesSupported: StrArr("scopes_supported"),
            ResponseTypesSupported: StrArr("response_types_supported"),
            GrantTypesSupported: StrArr("grant_types_supported"),
            TokenEndpointAuthMethodsSupported: StrArr("token_endpoint_auth_methods_supported"),
            CodeChallengeMethodsSupported: StrArr("code_challenge_methods_supported"),
            ResourceParameterSupported: resourceParameter,
            Raw: obj);
    }

    private static AuthorizationServerInfo Empty(string issuer, string error)
        => new(
            Issuer: issuer,
            Fetched: false,
            FetchError: error,
            AuthorizationEndpoint: null,
            TokenEndpoint: null,
            RegistrationEndpoint: null,
            IntrospectionEndpoint: null,
            RevocationEndpoint: null,
            JwksUri: null,
            ScopesSupported: [],
            ResponseTypesSupported: [],
            GrantTypesSupported: [],
            TokenEndpointAuthMethodsSupported: [],
            CodeChallengeMethodsSupported: [],
            ResourceParameterSupported: null,
            Raw: null);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
