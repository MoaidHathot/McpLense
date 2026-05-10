using System.Text.Json.Serialization;

namespace McpLense;

/// <summary>
/// Serialised on-disk representation of cached OAuth state for one MCP server.
/// Holds both the issued tokens and the (optional) DCR-registered client credentials so
/// subsequent runs do not re-register a new client on every invocation.
///
/// Stored encrypted via DPAPI on Windows (<c>%LOCALAPPDATA%\McpLense\tokens\&lt;name&gt;.bin</c>)
/// or as a <c>chmod 600</c> JSON file on Unix (<c>$XDG_DATA_HOME/mcplense/tokens/&lt;name&gt;.json</c>).
/// </summary>
internal sealed record OAuthCacheEntry(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("tokenEndpoint")] string TokenEndpoint,
    [property: JsonPropertyName("redirectUri")] string RedirectUri,
    [property: JsonPropertyName("issuer")] string? Issuer = null,
    [property: JsonPropertyName("clientSecret")] string? ClientSecret = null,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken = null,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("resourceUri")] string? ResourceUri = null,
    [property: JsonPropertyName("registrationEndpoint")] string? RegistrationEndpoint = null)
{
    /// <summary>
    /// Returns true when the cached access token is past or within <paramref name="skew"/> of expiry.
    /// Tokens with no <see cref="ExpiresAt"/> are considered non-expiring (returns false).
    /// </summary>
    public bool IsExpired(TimeSpan skew, DateTimeOffset? now = null)
    {
        if (ExpiresAt is null)
        {
            return false;
        }

        var reference = now ?? DateTimeOffset.UtcNow;
        return ExpiresAt.Value - skew <= reference;
    }
}
