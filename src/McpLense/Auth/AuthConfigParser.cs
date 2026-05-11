using System.Text.Json.Nodes;

namespace McpLense;

/// <summary>
/// Parses a per-server <c>auth</c> JSON block into a <see cref="ResolvedAuth"/> instance.
/// Performs environment-variable expansion on every string value via <see cref="EnvironmentExpander"/>
/// and validates conflicts with the literal <c>Authorization</c> header.
/// </summary>
internal sealed class AuthConfigParser
{
    private readonly EnvironmentExpander _expander;

    public AuthConfigParser(EnvironmentExpander expander)
    {
        _expander = expander ?? throw new ArgumentNullException(nameof(expander));
    }

    /// <summary>
    /// Parses the server's <c>auth</c> block, if present.
    /// </summary>
    /// <param name="serverObject">The parsed server JSON object.</param>
    /// <param name="serverName">Server name for error messages.</param>
    /// <param name="headers">Headers already extracted from the server (used to detect Authorization conflicts).</param>
    /// <returns>The resolved auth configuration, or null when no <c>auth</c> block is present.</returns>
    public ResolvedAuth? Parse(JsonObject serverObject, string serverName, IReadOnlyDictionary<string, string> headers)
    {
        if (serverObject["auth"] is not JsonObject authObject)
        {
            return null;
        }

        if (headers.ContainsKey("Authorization"))
        {
            throw new UserInputException(
                $"Server '{serverName}': cannot set both an 'auth' block and an explicit 'Authorization' header.");
        }

        var basePath = $"servers.{serverName}.auth";
        var typeRaw = GetExpandedString(authObject, "type", $"{basePath}.type");
        if (string.IsNullOrWhiteSpace(typeRaw))
        {
            throw new UserInputException($"{basePath}.type is required when an 'auth' block is set.");
        }

        var kind = ParseKind(typeRaw, basePath);

        return kind switch
        {
            AuthKind.Bearer => ParseBearer(authObject, basePath),
            AuthKind.OAuth => ParseOAuth(authObject, basePath),
            AuthKind.InteractiveBrowser => ParseInteractiveBrowser(authObject, basePath),
            _ => throw new UserInputException($"{basePath}.type '{typeRaw}' is not supported.")
        };
    }

    private static AuthKind ParseKind(string raw, string basePath)
    {
        return raw.ToLowerInvariant() switch
        {
            "bearer" => AuthKind.Bearer,
            "oauth" or "oauthdiscovery" => AuthKind.OAuth,
            "interactive-browser" or "interactivebrowser" => AuthKind.InteractiveBrowser,
            _ => throw new UserInputException(
                $"{basePath}.type '{raw}' is not recognised. Supported values: 'bearer', 'oauth', 'interactive-browser'.")
        };
    }

    private ResolvedAuth ParseBearer(JsonObject authObject, string basePath)
    {
        var token = GetExpandedString(authObject, "token", $"{basePath}.token");
        if (string.IsNullOrEmpty(token))
        {
            throw new UserInputException($"{basePath}.token is required when type is 'bearer'.");
        }

        return new ResolvedAuth(AuthKind.Bearer, Token: token);
    }

    private ResolvedAuth ParseInteractiveBrowser(JsonObject authObject, string basePath)
    {
        // Entra ID v2.0 interactive-browser flow via MSAL/Azure.Identity. clientId is required:
        // public-client GUIDs (e.g. the VS Code first-party client) cannot be auto-discovered, and
        // Entra does not support RFC 7591 Dynamic Client Registration. tenantId is optional; when
        // null MSAL falls back to "common" which accepts any work/school/personal account.
        var clientId = GetExpandedString(authObject, "clientId", $"{basePath}.clientId");
        if (string.IsNullOrEmpty(clientId))
        {
            throw new UserInputException(
                $"{basePath}.clientId is required when type is 'interactive-browser'. " +
                "Use the application (client) ID from your Entra app registration, or the VS Code " +
                "public client 'aebc6443-996d-45c2-90f0-388ff96faa56' for first-party Microsoft services.");
        }

        var scopes = ParseScopes(authObject, $"{basePath}.scopes");
        if (scopes is null || scopes.Count == 0)
        {
            throw new UserInputException(
                $"{basePath}.scopes is required when type is 'interactive-browser'. " +
                "Use '<application-id-uri>/.default' to request every statically-consented permission " +
                "for the target resource.");
        }

        var tenantId = GetExpandedString(authObject, "tenantId", $"{basePath}.tenantId");
        var cacheName = GetExpandedString(authObject, "cacheName", $"{basePath}.cacheName");
        var redirectUri = GetExpandedString(authObject, "redirectUri", $"{basePath}.redirectUri");

        return new ResolvedAuth(
            AuthKind.InteractiveBrowser,
            Scopes: scopes,
            RedirectUri: redirectUri,
            CacheName: cacheName,
            ClientId: clientId,
            TenantId: tenantId);
    }

    private ResolvedAuth ParseOAuth(JsonObject authObject, string basePath)
    {
        var scopes = ParseScopes(authObject, $"{basePath}.scopes");
        var redirectUri = GetExpandedString(authObject, "redirectUri", $"{basePath}.redirectUri");
        var cacheName = GetExpandedString(authObject, "cacheName", $"{basePath}.cacheName");
        var clientId = GetExpandedString(authObject, "clientId", $"{basePath}.clientId");
        var clientSecret = GetExpandedString(authObject, "clientSecret", $"{basePath}.clientSecret");
        var issuer = GetExpandedString(authObject, "issuer", $"{basePath}.issuer");
        var authorizationEndpoint = GetExpandedString(authObject, "authorizationEndpoint", $"{basePath}.authorizationEndpoint");
        var tokenEndpoint = GetExpandedString(authObject, "tokenEndpoint", $"{basePath}.tokenEndpoint");
        var registrationEndpoint = GetExpandedString(authObject, "registrationEndpoint", $"{basePath}.registrationEndpoint");
        var resourceMetadataUrl = GetExpandedString(authObject, "resourceMetadataUrl", $"{basePath}.resourceMetadataUrl");
        var resourceUri = GetExpandedString(authObject, "resourceUri", $"{basePath}.resourceUri");

        return new ResolvedAuth(
            AuthKind.OAuth,
            Scopes: scopes,
            RedirectUri: redirectUri,
            CacheName: cacheName,
            ClientId: clientId,
            ClientSecret: clientSecret,
            Issuer: issuer,
            AuthorizationEndpoint: authorizationEndpoint,
            TokenEndpoint: tokenEndpoint,
            RegistrationEndpoint: registrationEndpoint,
            ResourceMetadataUrl: resourceMetadataUrl,
            ResourceUri: resourceUri);
    }

    private IReadOnlyList<string>? ParseScopes(JsonObject authObject, string contextPath)
    {
        if (authObject["scopes"] is not JsonArray array)
        {
            return null;
        }

        var values = new List<string>();
        for (var index = 0; index < array.Count; index++)
        {
            var item = array[index];
            if (item is JsonValue value)
            {
                values.Add(_expander.Expand(value.GetValue<string>(), $"{contextPath}[{index}]"));
            }
        }

        return values;
    }

    private string? GetExpandedString(JsonObject obj, string property, string contextPath)
    {
        if (obj[property] is not JsonValue value)
        {
            return null;
        }

        return _expander.Expand(value.GetValue<string>(), contextPath);
    }
}
