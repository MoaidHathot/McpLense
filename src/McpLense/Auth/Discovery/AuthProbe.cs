using System.Net.Http.Headers;
using System.Text.Json;

namespace McpLense;

/// <summary>
/// Captures the (best-effort) authentication metadata advertised by an MCP server.
/// All fields may be null when the server doesn't speak RFC 9728 / OAuth discovery.
/// </summary>
/// <param name="RequiresAuth">
/// True when the probe saw concrete evidence that authentication is required (HTTP 401, or a
/// <c>WWW-Authenticate</c> header). Servers that respond 200 without auth headers leave this
/// false so callers can connect plain.
/// </param>
/// <param name="ResourceMetadataUrl">
/// The <c>resource_metadata</c> URL extracted from <c>WWW-Authenticate: Bearer</c>, when present.
/// </param>
/// <param name="Scopes">Scopes advertised by the protected-resource metadata document, if any.</param>
/// <param name="AuthorizationServers">
/// AS issuer URLs advertised by the protected-resource metadata document, if any.
/// </param>
internal sealed record AuthProbeResult(
    bool RequiresAuth = false,
    string? ResourceMetadataUrl = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? AuthorizationServers = null)
{
    public static AuthProbeResult Empty { get; } = new();

    /// <summary>True when the probe surfaced no useful metadata at all.</summary>
    public bool IsEmpty
        => !RequiresAuth
           && string.IsNullOrEmpty(ResourceMetadataUrl)
           && (Scopes is null || Scopes.Count == 0)
           && (AuthorizationServers is null || AuthorizationServers.Count == 0);
}

/// <summary>
/// Probes an MCP endpoint for RFC 9728 protected-resource metadata. The probe is intentionally
/// best-effort: any failure (no <c>WWW-Authenticate</c>, network error, malformed JSON) is
/// reported on stderr and the resolver falls through to cache-only auto-pick.
/// </summary>
internal interface IAuthProbe
{
    Task<AuthProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IAuthProbe"/>. Owns its own <see cref="HttpClient"/> when none is supplied.</summary>
internal sealed class AuthProbe : IAuthProbe, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Action<string> _writeStderr;

    public AuthProbe()
        : this(httpClient: null, writeStderr: null)
    {
    }

    /// <summary>For tests: inject a fake <see cref="HttpClient"/> and stderr sink.</summary>
    internal AuthProbe(HttpClient? httpClient, Action<string>? writeStderr)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler(), disposeHandler: true)
        {
            Timeout = DefaultTimeout
        };
        _writeStderr = writeStderr ?? Console.Error.WriteLine;
    }

    public async Task<AuthProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);

        // Step 1: probe the server itself for a 401 + WWW-Authenticate header. We send a HEAD
        // first (cheap, doesn't consume server-state) and fall back to GET for servers that
        // reject HEAD outright.
        HttpResponseMessage? response = null;
        try
        {
            response = await SendUnauthenticatedAsync(serverUrl, HttpMethod.Head, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 405 or 501)
            {
                response.Dispose();
                response = await SendUnauthenticatedAsync(serverUrl, HttpMethod.Get, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Real user-driven cancellation - propagate.
            response?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // Network failure, DNS error, HttpClient timeout (manifests as TaskCanceledException
            // even when the caller's token wasn't cancelled). All recoverable - fall through.
            _writeStderr($"AuthProbe: probing {serverUrl} failed ({ex.GetType().Name}: {ex.Message}); falling back to cache-only auto-pick.");
            response?.Dispose();
            return AuthProbeResult.Empty;
        }

        try
        {
            var isSuccessStatus = response.IsSuccessStatusCode;
            var hasAuthChallenge = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                                   || response.Headers.WwwAuthenticate.Count > 0;

            // Anything that isn't a clean 2xx is treated as "auth probably required". This is
            // conservative on purpose: servers that flake on HEAD (e.g. Agent365 sometimes
            // returns 503 to unauthenticated probes), block HEAD entirely, or return non-401
            // 4xx for missing credentials would otherwise be misdiagnosed as "no auth needed"
            // and connect plain. When profiles are loaded the cost of false-positive attach is
            // zero (handler just adds a Bearer header), while the cost of false-negative skip is
            // a confusing error from the runtime path.
            var requiresAuth = hasAuthChallenge || !isSuccessStatus;
            var resourceMetadataUrl = TryExtractResourceMetadataUrl(response);

            if (string.IsNullOrEmpty(resourceMetadataUrl))
            {
                if (!requiresAuth)
                {
                    // Server appears not to need auth at all.
                    return AuthProbeResult.Empty;
                }

                if (!hasAuthChallenge)
                {
                    // Server returned non-2xx but no explicit auth challenge. Could be a flake,
                    // could be "wrong endpoint", could be auth required behind a generic error.
                    // Flag for the caller and treat as auth-required so a loaded profile is
                    // attached and the runtime gets an authoritative answer.
                    _writeStderr($"AuthProbe: {serverUrl} returned {(int)response.StatusCode} on the unauthenticated probe; attaching the configured profile so the runtime hits the server with credentials.");
                }
                else
                {
                    _writeStderr($"AuthProbe: {serverUrl} returned no RFC 9728 'resource_metadata' header; falling back to cache-only auto-pick.");
                }

                return new AuthProbeResult(RequiresAuth: true);
            }

            var metadata = await FetchProtectedResourceMetadataAsync(resourceMetadataUrl!, cancellationToken).ConfigureAwait(false);
            return metadata with { RequiresAuth = true };
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendUnauthenticatedAsync(Uri url, HttpMethod method, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        // Make sure no inherited Authorization header sneaks in (HttpClient default is to keep
        // none, but be explicit).
        request.Headers.Authorization = null;
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the <c>resource_metadata</c> URL from a <c>WWW-Authenticate: Bearer</c> challenge
    /// per RFC 9728 §5.1. Returns null when the header is missing or malformed.
    /// </summary>
    internal static string? TryExtractResourceMetadataUrl(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (var challenge in response.Headers.WwwAuthenticate)
        {
            if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = ParseResourceMetadataParameter(challenge.Parameter);
            if (!string.IsNullOrEmpty(url))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the comma-separated parameter list of a <c>WWW-Authenticate</c> challenge looking
    /// for <c>resource_metadata="..."</c>. Tolerates extra parameters and surrounding whitespace.
    /// </summary>
    private static string? ParseResourceMetadataParameter(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return null;
        }

        // Walk key=value pairs respecting double-quoted values. Avoids a full RFC 7235 parser
        // since RFC 9728 only adds one parameter we care about.
        var index = 0;
        while (index < parameter.Length)
        {
            // Skip whitespace and leading comma between params.
            while (index < parameter.Length && (char.IsWhiteSpace(parameter[index]) || parameter[index] == ','))
            {
                index++;
            }

            var keyStart = index;
            while (index < parameter.Length && parameter[index] != '=' && parameter[index] != ',')
            {
                index++;
            }

            var key = parameter[keyStart..index].Trim();

            if (index >= parameter.Length || parameter[index] != '=')
            {
                continue;
            }

            // Skip '='
            index++;

            string value;
            if (index < parameter.Length && parameter[index] == '"')
            {
                index++; // skip opening quote
                var valueStart = index;
                while (index < parameter.Length && parameter[index] != '"')
                {
                    // Escaped quote (\")
                    if (parameter[index] == '\\' && index + 1 < parameter.Length)
                    {
                        index += 2;
                        continue;
                    }

                    index++;
                }

                value = parameter[valueStart..index];
                if (index < parameter.Length)
                {
                    index++; // skip closing quote
                }
            }
            else
            {
                var valueStart = index;
                while (index < parameter.Length && parameter[index] != ',')
                {
                    index++;
                }

                value = parameter[valueStart..index].Trim();
            }

            if (string.Equals(key, "resource_metadata", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private async Task<AuthProbeResult> FetchProtectedResourceMetadataAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var metadataUri))
        {
            _writeStderr($"AuthProbe: 'resource_metadata' URL '{url}' is not absolute; falling back to cache-only auto-pick.");
            return AuthProbeResult.Empty;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _writeStderr($"AuthProbe: protected-resource metadata at {metadataUri} returned {(int)response.StatusCode}; falling back to cache-only auto-pick.");
                return new AuthProbeResult(ResourceMetadataUrl: url);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseProtectedResourceMetadata(json, url);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _writeStderr($"AuthProbe: fetching protected-resource metadata at {metadataUri} failed ({ex.GetType().Name}: {ex.Message}); falling back to cache-only auto-pick.");
            return new AuthProbeResult(ResourceMetadataUrl: url);
        }
    }

    private AuthProbeResult ParseProtectedResourceMetadata(string json, string resourceMetadataUrl)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var scopes = ReadStringArray(root, "scopes_supported");
            var authorizationServers = ReadStringArray(root, "authorization_servers");

            return new AuthProbeResult(
                ResourceMetadataUrl: resourceMetadataUrl,
                Scopes: scopes,
                AuthorizationServers: authorizationServers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _writeStderr($"AuthProbe: protected-resource metadata at {resourceMetadataUrl} is not valid JSON ({ex.GetType().Name}: {ex.Message}); falling back to cache-only auto-pick.");
            return new AuthProbeResult(ResourceMetadataUrl: resourceMetadataUrl);
        }
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    values.Add(text);
                }
            }
        }

        return values.Count == 0 ? null : values;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
