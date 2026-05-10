using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpLense;

/// <summary>
/// RFC 7591 Dynamic Client Registration. POSTs the McpLense client metadata to the issuer's
/// <c>registration_endpoint</c> and returns the resulting <c>client_id</c> (and optional
/// <c>client_secret</c>).
///
/// McpLense registers itself as a public native application with PKCE and refresh tokens; the
/// resulting credentials are cached alongside the issued tokens so subsequent runs reuse the
/// same client_id rather than registering a new one on every invocation.
/// </summary>
internal sealed class DynamicClientRegistration
{
    /// <summary>Application name presented to the authorization server during DCR.</summary>
    public const string ClientName = "McpLense";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public DynamicClientRegistration(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Registers a new client with the authorization server.
    /// </summary>
    /// <param name="registrationEndpoint">DCR endpoint URI (from ASM <c>registration_endpoint</c>).</param>
    /// <param name="redirectUri">Loopback redirect URI to register.</param>
    /// <param name="scopes">Optional scopes to request at registration time.</param>
    public async Task<DynamicClientRegistrationResponse> RegisterAsync(
        Uri registrationEndpoint,
        string redirectUri,
        IReadOnlyList<string>? scopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registrationEndpoint);
        if (string.IsNullOrEmpty(redirectUri))
        {
            throw new ArgumentException("Redirect URI is required.", nameof(redirectUri));
        }

        var request = new DynamicClientRegistrationRequest
        {
            ClientName = ClientName,
            RedirectUris = [redirectUri],
            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"],
            TokenEndpointAuthMethod = "none",
            ApplicationType = "native",
            Scope = scopes is { Count: > 0 } ? string.Join(' ', scopes) : null
        };

        using var content = JsonContent.Create(request, options: JsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint) { Content = content };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            throw new McpLenseAuthException(
                $"Dynamic Client Registration at '{registrationEndpoint}' failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}".TrimEnd());
        }

        DynamicClientRegistrationResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<DynamicClientRegistrationResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new McpLenseAuthException(
                $"DCR response from '{registrationEndpoint}' was not valid JSON: {ex.Message}", ex);
        }

        if (parsed is null || string.IsNullOrEmpty(parsed.ClientId))
        {
            throw new McpLenseAuthException(
                $"DCR response from '{registrationEndpoint}' did not include a 'client_id'.");
        }

        return parsed;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(body) ? string.Empty : body;
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>RFC 7591 client-registration request body.</summary>
internal sealed record DynamicClientRegistrationRequest
{
    [JsonPropertyName("client_name")]
    public string? ClientName { get; init; }

    [JsonPropertyName("redirect_uris")]
    public IReadOnlyList<string>? RedirectUris { get; init; }

    [JsonPropertyName("grant_types")]
    public IReadOnlyList<string>? GrantTypes { get; init; }

    [JsonPropertyName("response_types")]
    public IReadOnlyList<string>? ResponseTypes { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; init; }

    [JsonPropertyName("application_type")]
    public string? ApplicationType { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>RFC 7591 client-registration response body. Only fields McpLense consumes are bound.</summary>
internal sealed record DynamicClientRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; init; }

    [JsonPropertyName("registration_access_token")]
    public string? RegistrationAccessToken { get; init; }

    [JsonPropertyName("client_id_issued_at")]
    public long? ClientIdIssuedAt { get; init; }

    [JsonPropertyName("client_secret_expires_at")]
    public long? ClientSecretExpiresAt { get; init; }
}
