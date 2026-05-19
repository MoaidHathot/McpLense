using System.Net.Http.Headers;
using System.Text.Json;
using McpLense.Scanning.TargetResolution;

namespace McpLense;

/// <summary>
/// Captures the (best-effort) authentication metadata advertised by an MCP server.
/// All fields may be null when the server doesn't speak RFC 9728 / OAuth discovery.
/// </summary>
/// <param name="RequiresAuth">
/// True when the probe saw concrete evidence that authentication is required (HTTP 401, a
/// <c>WWW-Authenticate</c> header, or any non-2xx response from the unauthenticated probe).
/// </param>
/// <param name="Inconclusive">
/// True when the probe could not reach a definitive answer (network error, timeout, malformed
/// response). Callers with loaded profiles should err on the side of attaching one in this case
/// rather than connecting plain.
/// </param>
/// <param name="ResourceMetadataUrl">
/// The <c>resource_metadata</c> URL extracted from <c>WWW-Authenticate: Bearer</c>, when present.
/// </param>
/// <param name="Scopes">Scopes advertised by the protected-resource metadata document, if any.</param>
/// <param name="AuthorizationServers">
/// AS issuer URLs advertised by the protected-resource metadata document, if any.
/// </param>
/// <param name="Resource">
/// The <c>resource</c> identifier advertised by the protected-resource metadata document, if any.
/// Per RFC 9728 §3 this is the canonical URI the resource server expects in tokens'
/// <c>aud</c> claim and serves as the FQN prefix for bare scope names (e.g. promoting
/// <c>"User.Read.All"</c> to <c>"https://resource/User.Read.All"</c> when the auth server
/// requires fully-qualified scope identifiers).
/// </param>
/// <param name="StatusCode">
/// HTTP status code returned by the unauthenticated probe, when the probe reached the server.
/// <c>null</c> when the network call failed before a response was received.
/// </param>
/// <param name="WwwAuthenticate">
/// Raw, comma-joined <c>WWW-Authenticate</c> header value(s) from the unauthenticated probe.
/// Preserved verbatim so callers (like the auth-scan command) can classify servers that
/// challenge with non-Bearer schemes or that lack RFC 9728 metadata.
/// </param>
internal sealed record AuthProbeResult(
    bool RequiresAuth = false,
    bool Inconclusive = false,
    string? ResourceMetadataUrl = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? AuthorizationServers = null,
    string? Resource = null,
    int? StatusCode = null,
    string? WwwAuthenticate = null)
{
    public static AuthProbeResult Empty { get; } = new();

    /// <summary>
    /// True when the probe surfaced no useful auth signal AND ran to completion (i.e. the
    /// server answered with a clean 2xx response and no auth headers). Network failures or
    /// other inconclusive outcomes leave this false so callers don't mistake them for a
    /// confirmed "no auth needed" signal.
    /// </summary>
    public bool IsEmpty
        => !RequiresAuth
           && !Inconclusive
           && string.IsNullOrEmpty(ResourceMetadataUrl)
           && (Scopes is null || Scopes.Count == 0)
           && (AuthorizationServers is null || AuthorizationServers.Count == 0)
           && string.IsNullOrEmpty(Resource)
           && string.IsNullOrEmpty(WwwAuthenticate);
}

/// <summary>
/// Probes an MCP endpoint for RFC 9728 protected-resource metadata. The probe is intentionally
/// best-effort: any failure (no <c>WWW-Authenticate</c>, network error, malformed JSON) is
/// reported on stderr and the resolver falls through to cache-only auto-pick.
/// </summary>
internal interface IAuthProbe
{
    Task<AuthProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Same as <see cref="ProbeAsync(Uri, CancellationToken)"/> but with optional per-target
    /// headers attached to the outbound probe (honoured only when <paramref name="scope"/> is
    /// <see cref="TargetScope.All"/>). Same-origin fetches (e.g. the protected-resource
    /// metadata document, when its URL matches the MCP host) get the headers too;
    /// cross-origin fetches do not.
    /// </summary>
    /// <remarks>
    /// Default implementation delegates to the bare overload so existing implementations
    /// (test stubs in particular) keep working unchanged. The concrete <see cref="AuthProbe"/>
    /// overrides this with the real header-aware behaviour.
    /// </remarks>
    Task<AuthProbeResult> ProbeAsync(
        Uri serverUrl,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        TargetScope scope,
        CancellationToken cancellationToken)
        => ProbeAsync(serverUrl, cancellationToken);
}

/// <summary>Default <see cref="IAuthProbe"/>. Owns its own <see cref="HttpClient"/> when none is supplied.</summary>
internal sealed class AuthProbe : IAuthProbe, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Action<string> _writeStderr;

    // Per-instance memoizer: the executor and the resolver both call ProbeAsync for the same
    // server URL (once for profile narrowing, once for scope substitution). Caching keeps that
    // to a single round-trip without forcing a redesign of the call sites. The cache key
    // includes the additional headers so two scans with different per-target overlays don't
    // collide on the same URL.
    private readonly Dictionary<(Uri, string), AuthProbeResult> _cache = new();

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

    public Task<AuthProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken)
        => ProbeAsync(serverUrl, additionalHeaders: null, scope: TargetScope.All, cancellationToken);

    public async Task<AuthProbeResult> ProbeAsync(
        Uri serverUrl,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        TargetScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);

        // Cache key includes the FULL header set so two scans with different per-target
        // headers (e.g. two organizations under the same MCP host) don't reuse each other's
        // probe result. Cheap: cache hits are still O(1) on the cache size and probes are
        // rare to begin with.
        var cacheKey = (serverUrl, ComputeHeaderKey(additionalHeaders, scope));
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var fresh = await ProbeUncachedAsync(serverUrl, additionalHeaders, scope, cancellationToken).ConfigureAwait(false);
        _cache[cacheKey] = fresh;
        return fresh;
    }

    private static string ComputeHeaderKey(IReadOnlyDictionary<string, string>? headers, TargetScope scope)
    {
        if (scope != TargetScope.All || headers is null || headers.Count == 0)
        {
            return "<none>";
        }

        return string.Join('|', headers.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }

    private async Task<AuthProbeResult> ProbeUncachedAsync(
        Uri serverUrl,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        TargetScope scope,
        CancellationToken cancellationToken)
    {
        // Step 1: probe the server for a 401 + WWW-Authenticate header. We use GET (not HEAD)
        // because some MCP servers (Agent365 most notably) HANG on HEAD requests for >30s
        // before timing out, while responding to GET in well under a second with the same auth
        // metadata. HttpCompletionOption.ResponseHeadersRead means we read the response headers
        // and dispose without downloading the body, so the cost is comparable to HEAD anyway.
        HttpResponseMessage? response = null;
        try
        {
            response = await SendUnauthenticatedAsync(serverUrl, HttpMethod.Get, additionalHeaders, scope, cancellationToken).ConfigureAwait(false);
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
            // even when the caller's token wasn't cancelled). Inconclusive: callers with loaded
            // profiles should still attach one rather than connecting plain.
            _writeStderr($"AuthProbe: probing {serverUrl} failed ({ex.GetType().Name}: {ex.Message}); attaching the configured profile so the runtime hits the server with credentials.");
            response?.Dispose();
            return new AuthProbeResult(Inconclusive: true);
        }

        try
        {
            var statusCode = (int)response.StatusCode;
            var wwwAuthenticate = JoinWwwAuthenticate(response);
            var hasAuthChallenge = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                                   || response.Headers.WwwAuthenticate.Count > 0;
            var isSuccessStatus = response.IsSuccessStatusCode;
            var resourceMetadataUrl = TryExtractResourceMetadataUrl(response);

            if (string.IsNullOrEmpty(resourceMetadataUrl))
            {
                if (hasAuthChallenge)
                {
                    // Server explicitly told us auth is needed but didn't point us at metadata.
                    _writeStderr($"AuthProbe: {serverUrl} returned no RFC 9728 'resource_metadata' header; falling back to cache-only auto-pick.");
                    return new AuthProbeResult(
                        RequiresAuth: true,
                        StatusCode: statusCode,
                        WwwAuthenticate: wwwAuthenticate);
                }

                if (isSuccessStatus)
                {
                    // Clean 2xx with no auth headers - server genuinely doesn't need auth.
                    return new AuthProbeResult(StatusCode: statusCode);
                }

                // Non-2xx without an auth challenge. Could be a flake (Agent365 sometimes returns
                // 503 to unauthenticated HEAD), a wrong endpoint, or auth-required behind a
                // generic error. Mark as Inconclusive so callers with loaded profiles attach one
                // and the runtime gets an authoritative answer, but DON'T claim auth is required
                // (we don't know that).
                _writeStderr($"AuthProbe: {serverUrl} returned {(int)response.StatusCode} on the unauthenticated probe; result is inconclusive.");
                return new AuthProbeResult(
                    Inconclusive: true,
                    StatusCode: statusCode,
                    WwwAuthenticate: wwwAuthenticate);
            }

            // RFC 9728 metadata URL: same-origin fetches honour the per-target overlay,
            // cross-origin fetches strictly do not (per the security rule).
            var metadata = await FetchProtectedResourceMetadataAsync(
                resourceMetadataUrl!,
                serverUrl,
                additionalHeaders,
                scope,
                cancellationToken).ConfigureAwait(false);
            return metadata with
            {
                RequiresAuth = true,
                StatusCode = statusCode,
                WwwAuthenticate = wwwAuthenticate
            };
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Joins every <c>WWW-Authenticate</c> challenge advertised by the response into a single
    /// comma-separated string. Returns <c>null</c> when the response carried no challenge.
    /// </summary>
    private static string? JoinWwwAuthenticate(HttpResponseMessage response)
    {
        if (response.Headers.WwwAuthenticate.Count == 0)
        {
            return null;
        }

        return string.Join(", ", response.Headers.WwwAuthenticate.Select(static challenge =>
            string.IsNullOrEmpty(challenge.Parameter)
                ? challenge.Scheme
                : $"{challenge.Scheme} {challenge.Parameter}"));
    }

    private async Task<HttpResponseMessage> SendUnauthenticatedAsync(
        Uri url,
        HttpMethod method,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        TargetScope scope,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        // Make sure no inherited Authorization header sneaks in (HttpClient default is to keep
        // none, but be explicit).
        request.Headers.Authorization = null;

        // Per-target headers (scope=all) ride along with the unauthenticated probe so that
        // a server which gates everything behind, say, x-mcp-ec-organization can still reply
        // with the proper RFC 9728 challenge instead of an opaque 400.
        if (scope == TargetScope.All && additionalHeaders is { Count: > 0 })
        {
            foreach (var (name, value) in additionalHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

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

    private async Task<AuthProbeResult> FetchProtectedResourceMetadataAsync(
        string url,
        Uri serverUrl,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        TargetScope scope,
        CancellationToken cancellationToken)
    {
        // RFC 9728 §3 says the resource_metadata URL is always an absolute HTTPS URL (HTTP is
        // tolerated for non-prod environments). We deliberately reject anything else: relative
        // URLs, file://, mailto:, custom schemes, etc. Without the explicit scheme check this
        // guard is platform-dependent - on Linux, Uri.TryCreate("/relative", UriKind.Absolute,
        // ...) succeeds with a file:///relative URI, while the same call returns false on
        // Windows. Enforcing http(s) keeps the validation portable AND spec-aligned.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var metadataUri)
            || (metadataUri.Scheme != Uri.UriSchemeHttp && metadataUri.Scheme != Uri.UriSchemeHttps))
        {
            _writeStderr($"AuthProbe: 'resource_metadata' URL '{url}' is not an absolute http(s) URL; falling back to cache-only auto-pick.");
            return new AuthProbeResult(Inconclusive: true);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Forward per-target headers only when same-origin (the metadata document
            // usually lives at /.well-known on the SAME MCP host; cross-origin would be
            // unusual and would imply sending MCP-server headers to a different host - which
            // we forbid as a security rule).
            if (scope == TargetScope.All
                && additionalHeaders is { Count: > 0 }
                && IsSameOrigin(serverUrl, metadataUri))
            {
                foreach (var (name, value) in additionalHeaders)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

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

    private static bool IsSameOrigin(Uri a, Uri b)
        => string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
           && a.Port == b.Port;

    private AuthProbeResult ParseProtectedResourceMetadata(string json, string resourceMetadataUrl)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var scopes = ReadStringArray(root, "scopes_supported");
            var authorizationServers = ReadStringArray(root, "authorization_servers");
            var resource = ReadString(root, "resource");

            return new AuthProbeResult(
                ResourceMetadataUrl: resourceMetadataUrl,
                Scopes: scopes,
                AuthorizationServers: authorizationServers,
                Resource: resource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _writeStderr($"AuthProbe: protected-resource metadata at {resourceMetadataUrl} is not valid JSON ({ex.GetType().Name}: {ex.Message}); falling back to cache-only auto-pick.");
            return new AuthProbeResult(ResourceMetadataUrl: resourceMetadataUrl);
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
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
