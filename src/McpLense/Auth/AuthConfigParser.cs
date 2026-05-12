using System.Text.Json.Nodes;

namespace McpLense;

/// <summary>
/// Parses the top-level <c>authProfiles</c> array from a profile config file into a list of
/// named <see cref="AuthProfile"/> entries. Performs environment-variable expansion on every
/// string value via <see cref="EnvironmentExpander"/> and validates per-kind required fields.
/// </summary>
internal sealed class AuthConfigParser
{
    private readonly EnvironmentExpander _expander;

    public AuthConfigParser(EnvironmentExpander expander)
    {
        _expander = expander ?? throw new ArgumentNullException(nameof(expander));
    }

    /// <summary>
    /// Parses every entry in <c>authProfiles</c>. Returns an empty list when the array is missing
    /// or empty so callers can merge results across multiple files.
    /// </summary>
    /// <param name="root">Parsed top-level JSON object of a profile config file.</param>
    /// <returns>One <see cref="AuthProfile"/> per JSON entry, in source order.</returns>
    public IReadOnlyList<AuthProfile> ParseAuthProfiles(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root["authProfiles"] is not JsonArray array)
        {
            return [];
        }

        var profiles = new List<AuthProfile>(array.Count);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject entry)
            {
                throw new UserInputException($"authProfiles[{index}] must be a JSON object.");
            }

            profiles.Add(ParseProfile(entry, index));
        }

        return profiles;
    }

    private AuthProfile ParseProfile(JsonObject profileObject, int index)
    {
        var nameRaw = GetExpandedString(profileObject, "name", $"authProfiles[{index}].name");
        if (string.IsNullOrWhiteSpace(nameRaw))
        {
            throw new UserInputException(
                $"authProfiles[{index}].name is required (give the profile a unique identifier such as 'agent365').");
        }

        if (profileObject["auth"] is not JsonObject authObject)
        {
            throw new UserInputException($"authProfiles[{index}].auth is required.");
        }

        var basePath = $"authProfiles[{nameRaw}].auth";
        var typeRaw = GetExpandedString(authObject, "type", $"{basePath}.type");
        if (string.IsNullOrWhiteSpace(typeRaw))
        {
            throw new UserInputException($"{basePath}.type is required.");
        }

        var kind = ParseKind(typeRaw, basePath);
        var auth = kind switch
        {
            AuthKind.Bearer => ParseBearer(authObject, basePath),
            AuthKind.OAuth => ParseOAuth(authObject, basePath),
            AuthKind.InteractiveBrowser => ParseInteractiveBrowser(authObject, basePath, defaultCacheName: nameRaw),
            AuthKind.AzureCli => ParseAzureCli(authObject, basePath),
            _ => throw new UserInputException($"{basePath}.type '{typeRaw}' is not supported.")
        };

        return new AuthProfile(nameRaw, auth);
    }

    private static AuthKind ParseKind(string raw, string basePath)
    {
        return raw.ToLowerInvariant() switch
        {
            "bearer" => AuthKind.Bearer,
            "oauth" or "oauthdiscovery" => AuthKind.OAuth,
            "interactive-browser" or "interactivebrowser" => AuthKind.InteractiveBrowser,
            "azure-cli" or "azurecli" or "az-cli" or "azcli" => AuthKind.AzureCli,
            _ => throw new UserInputException(
                $"{basePath}.type '{raw}' is not recognised. Supported values: 'bearer', 'oauth', 'interactive-browser', 'azure-cli'.")
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

    private ResolvedAuth ParseInteractiveBrowser(JsonObject authObject, string basePath, string defaultCacheName)
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

        // Default cacheName to the profile name so each profile gets its own MSAL cache file out
        // of the box. Users can opt into a shared cache (e.g. "mcp-proxy") by setting cacheName
        // explicitly.
        return new ResolvedAuth(
            AuthKind.InteractiveBrowser,
            Scopes: scopes,
            RedirectUri: redirectUri,
            CacheName: string.IsNullOrEmpty(cacheName) ? defaultCacheName : cacheName,
            ClientId: clientId,
            TenantId: tenantId);
    }

    private ResolvedAuth ParseAzureCli(JsonObject authObject, string basePath)
    {
        // AzureCliCredential delegates to `az account get-access-token --resource <scope>`.
        // It needs scopes (mandatory) and optionally a tenantId; the client id is fixed by the
        // Azure CLI's own pre-registered first-party app, so we do NOT accept clientId here.
        // There is no on-disk cache to manage either - the CLI maintains its own session.
        var scopes = ParseScopes(authObject, $"{basePath}.scopes");
        if (scopes is null || scopes.Count == 0)
        {
            throw new UserInputException(
                $"{basePath}.scopes is required when type is 'azure-cli'. " +
                "Use '<application-id-uri>/.default' to ask Azure CLI for a token carrying every " +
                "statically-consented permission on that resource.");
        }

        if (authObject.ContainsKey("clientId"))
        {
            throw new UserInputException(
                $"{basePath}.clientId is not accepted with type 'azure-cli'. The Azure CLI uses its " +
                "own pre-registered first-party client id; switch to 'interactive-browser' if you " +
                "need to control the client id.");
        }

        if (authObject.ContainsKey("cacheName"))
        {
            throw new UserInputException(
                $"{basePath}.cacheName is not accepted with type 'azure-cli'. The Azure CLI manages " +
                "its own credentials cache (see 'az account list').");
        }

        var tenantId = GetExpandedString(authObject, "tenantId", $"{basePath}.tenantId");

        return new ResolvedAuth(
            AuthKind.AzureCli,
            Scopes: scopes,
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
