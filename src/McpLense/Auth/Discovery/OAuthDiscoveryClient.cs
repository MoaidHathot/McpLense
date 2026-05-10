using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpLense;

/// <summary>
/// Discovers OAuth metadata for an MCP server per the spec:
/// <list type="number">
///   <item>RFC 9728 §3.1 &mdash; <c>{resource}/.well-known/oauth-protected-resource</c> Protected
///   Resource Metadata (PRM). The PRM yields the trusted authorization-server issuer.</item>
///   <item>Authorization Server Metadata (ASM). Tried in three forms, in order:
///   <list type="number">
///     <item>RFC 8414 §3.1 path-insert: <c>{issuer_origin}/.well-known/oauth-authorization-server{issuer_path}</c> (strict spec).</item>
///     <item>RFC 8414 path-append variant: <c>{issuer}/.well-known/oauth-authorization-server</c>.</item>
///     <item>OIDC Discovery 1.0: <c>{issuer}/.well-known/openid-configuration</c>. Per RFC 8414 §5,
///     OIDC documents are a superset of ASM for the fields McpLense consumes, so this is a valid
///     fallback for OIDC-only authorization servers (e.g. Microsoft Entra ID v2.0).</item>
///   </list>
///   The ASM yields the authorization, token, and (optional) registration endpoints, plus
///   supported scopes.</item>
/// </list>
///
/// Callers may bypass either step by supplying explicit endpoints in the resolved config; this
/// client encapsulates the network round-trips and JSON parsing only.
/// </summary>
internal sealed class OAuthDiscoveryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public OAuthDiscoveryClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Fetches Protected Resource Metadata. Returns null when the server does not advertise it
    /// (404 is treated as "not advertised" so we can fall back to direct issuer discovery).
    /// </summary>
    public async Task<ProtectedResourceMetadata?> FetchProtectedResourceMetadataAsync(
        Uri prmUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prmUri);

        using var request = new HttpRequestMessage(HttpMethod.Get, prmUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new McpLenseAuthException(
                $"Protected Resource Metadata fetch from '{prmUri}' failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<ProtectedResourceMetadata>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new McpLenseAuthException(
                $"Protected Resource Metadata at '{prmUri}' was not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fetches Authorization Server Metadata. Tries (in order):
    /// <list type="number">
    ///   <item>RFC 8414 strict path-insert: <c>{issuer_origin}/.well-known/oauth-authorization-server{issuer_path}</c>.</item>
    ///   <item>RFC 8414 path-append variant: <c>{issuer}/.well-known/oauth-authorization-server</c>.</item>
    ///   <item>OIDC Discovery: <c>{issuer}/.well-known/openid-configuration</c>.</item>
    /// </list>
    ///
    /// <para>
    /// HTTP 404 (and any other non-2xx) on a given form falls through to the next form. A 2xx
    /// response with malformed JSON, or with missing <c>authorization_endpoint</c>/<c>token_endpoint</c>,
    /// stops the ladder and surfaces the failure — the server clearly meant to respond at that URL.
    /// If all three forms exhaust, an <see cref="McpLenseAuthException"/> is raised that lists every
    /// URL attempted with its outcome, to ease diagnosis.
    /// </para>
    /// </summary>
    public async Task<AuthorizationServerMetadata> FetchAuthorizationServerMetadataAsync(
        Uri issuer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        var candidates = new[]
        {
            BuildAsmUri(issuer),
            BuildAsmAppendUri(issuer),
            BuildOidcUri(issuer)
        };

        var attempts = new List<string>(capacity: candidates.Length);

        foreach (var candidate in candidates)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                attempts.Add($"{candidate} -> HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                continue;
            }

            AuthorizationServerMetadata? metadata;
            try
            {
                metadata = await response.Content.ReadFromJsonAsync<AuthorizationServerMetadata>(JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new McpLenseAuthException(
                    $"Authorization Server Metadata at '{candidate}' was not valid JSON: {ex.Message}", ex);
            }

            if (metadata is null)
            {
                throw new McpLenseAuthException(
                    $"Authorization Server Metadata at '{candidate}' was empty.");
            }

            if (string.IsNullOrEmpty(metadata.AuthorizationEndpoint) || string.IsNullOrEmpty(metadata.TokenEndpoint))
            {
                throw new McpLenseAuthException(
                    $"Authorization Server Metadata at '{candidate}' is missing 'authorization_endpoint' or 'token_endpoint'.");
            }

            return metadata;
        }

        throw new McpLenseAuthException(
            $"Authorization Server Metadata could not be located for issuer '{issuer}'. Tried:" +
            Environment.NewLine +
            "  - " + string.Join(Environment.NewLine + "  - ", attempts));
    }

    /// <summary>
    /// Default Protected Resource Metadata URL for an MCP resource:
    /// <c>{resource_origin}/.well-known/oauth-protected-resource{resource_path}</c>
    /// per RFC 9728 §3.
    /// </summary>
    public static Uri BuildPrmUri(Uri resourceUri)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);

        var origin = $"{resourceUri.Scheme}://{resourceUri.Authority}";
        var path = resourceUri.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return new Uri(origin + "/.well-known/oauth-protected-resource");
        }

        return new Uri(origin + "/.well-known/oauth-protected-resource" + path);
    }

    /// <summary>
    /// Default Authorization Server Metadata URL for an issuer:
    /// <c>{issuer_origin}/.well-known/oauth-authorization-server{issuer_path}</c>
    /// per RFC 8414 §3.
    /// </summary>
    public static Uri BuildAsmUri(Uri issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        var origin = $"{issuer.Scheme}://{issuer.Authority}";
        var path = issuer.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return new Uri(origin + "/.well-known/oauth-authorization-server");
        }

        return new Uri(origin + "/.well-known/oauth-authorization-server" + path);
    }

    /// <summary>
    /// Path-append variant of the Authorization Server Metadata URL:
    /// <c>{issuer}/.well-known/oauth-authorization-server</c>. Some authorization servers serve
    /// ASM at this OIDC-style location instead of the strict RFC 8414 path-insert form.
    /// </summary>
    public static Uri BuildAsmAppendUri(Uri issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        return BuildWellKnownAppendUri(issuer, "oauth-authorization-server");
    }

    /// <summary>
    /// OIDC Discovery 1.0 metadata URL: <c>{issuer}/.well-known/openid-configuration</c>. Used as
    /// the final ASM fallback for OIDC-only authorization servers (notably Microsoft Entra ID v2.0,
    /// which does not serve <c>oauth-authorization-server</c> in either form). Per RFC 8414 §5,
    /// OIDC documents are a superset of ASM for the fields McpLense reads.
    /// </summary>
    public static Uri BuildOidcUri(Uri issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        return BuildWellKnownAppendUri(issuer, "openid-configuration");
    }

    private static Uri BuildWellKnownAppendUri(Uri issuer, string suffix)
    {
        var origin = $"{issuer.Scheme}://{issuer.Authority}";
        var path = issuer.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return new Uri(origin + "/.well-known/" + suffix);
        }

        var trimmed = path.TrimEnd('/');
        return new Uri(origin + trimmed + "/.well-known/" + suffix);
    }
}

/// <summary>
/// RFC 9728 Protected Resource Metadata document. Only fields McpLense consumes are bound.
/// </summary>
internal sealed record ProtectedResourceMetadata
{
    [JsonPropertyName("resource")]
    public string? Resource { get; init; }

    [JsonPropertyName("authorization_servers")]
    public IReadOnlyList<string>? AuthorizationServers { get; init; }

    [JsonPropertyName("scopes_supported")]
    public IReadOnlyList<string>? ScopesSupported { get; init; }

    [JsonPropertyName("bearer_methods_supported")]
    public IReadOnlyList<string>? BearerMethodsSupported { get; init; }
}

/// <summary>
/// RFC 8414 Authorization Server Metadata document. Only fields McpLense consumes are bound.
/// </summary>
internal sealed record AuthorizationServerMetadata
{
    [JsonPropertyName("issuer")]
    public string? Issuer { get; init; }

    [JsonPropertyName("authorization_endpoint")]
    public string? AuthorizationEndpoint { get; init; }

    [JsonPropertyName("token_endpoint")]
    public string? TokenEndpoint { get; init; }

    [JsonPropertyName("registration_endpoint")]
    public string? RegistrationEndpoint { get; init; }

    [JsonPropertyName("scopes_supported")]
    public IReadOnlyList<string>? ScopesSupported { get; init; }

    [JsonPropertyName("code_challenge_methods_supported")]
    public IReadOnlyList<string>? CodeChallengeMethodsSupported { get; init; }

    [JsonPropertyName("grant_types_supported")]
    public IReadOnlyList<string>? GrantTypesSupported { get; init; }

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public IReadOnlyList<string>? TokenEndpointAuthMethodsSupported { get; init; }
}
