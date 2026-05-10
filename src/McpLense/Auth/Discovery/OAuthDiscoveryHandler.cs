using System.Net;
using System.Net.Http.Headers;

namespace McpLense;

/// <summary>
/// <see cref="DelegatingHandler"/> that injects OAuth bearer tokens on outbound HTTP requests.
///
/// Strategy:
/// <list type="number">
///   <item>Lazily ensures a valid access token before each request: load from cache, refresh if
///         expired, run the interactive authorization-code+PKCE flow on cache miss/refresh failure.</item>
///   <item>Always overwrites any pre-existing <c>Authorization</c> header with the resolved bearer.</item>
///   <item>On HTTP 401, performs a single retry: forces a refresh (or interactive flow) and
///         re-issues the original request once.</item>
/// </list>
///
/// <para>
/// A <see cref="SemaphoreSlim"/> serialises token acquisition so concurrent requests through the
/// same <see cref="HttpClient"/> do not race the browser flow.
/// </para>
///
/// <para>
/// Setting <c>MCPLENSE_NO_INTERACTIVE_FLOW=1</c> disables the browser fallback; the handler will
/// surface a clear <see cref="McpLenseAuthException"/> instructing the user to run with
/// <c>--login</c> instead. This is intended for headless/CI environments.
/// </para>
/// </summary>
internal sealed class OAuthDiscoveryHandler : DelegatingHandler
{
    private static readonly TimeSpan DefaultSkew = TimeSpan.FromSeconds(60);

    private readonly ResolvedAuth _auth;
    private readonly Uri _resourceUri;
    private readonly IOAuthTokenCache _cache;
    private readonly string _cacheKey;
    private readonly OAuthFlowOrchestrator _orchestrator;
    private readonly TimeSpan _skew;
    private readonly bool _interactiveAllowed;
    private readonly IDisposable? _ownedResource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OAuthCacheEntry? _current;

    public OAuthDiscoveryHandler(
        ResolvedAuth auth,
        Uri resourceUri,
        IOAuthTokenCache cache,
        OAuthFlowOrchestrator orchestrator,
        TimeSpan? skew = null,
        bool? interactiveAllowed = null,
        IDisposable? ownedResource = null)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _resourceUri = resourceUri ?? throw new ArgumentNullException(nameof(resourceUri));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _cacheKey = IOAuthTokenCache.ResolveCacheKey(auth.CacheName, resourceUri.ToString());
        _skew = skew ?? DefaultSkew;
        _interactiveAllowed = interactiveAllowed ?? !string.Equals(
            Environment.GetEnvironmentVariable("MCPLENSE_NO_INTERACTIVE_FLOW"),
            "1",
            StringComparison.Ordinal);
        _ownedResource = ownedResource;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entry = await EnsureTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        ApplyBearer(request, entry.AccessToken);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // Reactive 401: dispose the failed response, force a refresh/interactive flow, retry once.
        response.Dispose();

        var refreshed = await EnsureTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        var retry = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        ApplyBearer(retry, refreshed.AccessToken);

        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a non-expired token, hitting the cache → refresh → interactive flow ladder as needed.
    /// Serialised via <see cref="_gate"/> so concurrent in-flight requests share a single acquisition.
    /// </summary>
    private async Task<OAuthCacheEntry> EnsureTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Fast path: another concurrent caller already obtained a fresh token while we waited.
            if (!forceRefresh && _current is not null && !_current.IsExpired(_skew))
            {
                return _current;
            }

            var loaded = _current ?? await _cache.LoadAsync(_cacheKey, cancellationToken).ConfigureAwait(false);

            if (!forceRefresh && loaded is not null && !loaded.IsExpired(_skew))
            {
                _current = loaded;
                return loaded;
            }

            // Token absent or stale: prefer a refresh-token grant when we have one.
            if (loaded is not null && !string.IsNullOrEmpty(loaded.RefreshToken))
            {
                var refreshed = await _orchestrator
                    .TryRefreshAsync(loaded, _auth, _resourceUri, cancellationToken)
                    .ConfigureAwait(false);

                if (refreshed is not null && !refreshed.IsExpired(_skew))
                {
                    _current = refreshed;
                    return refreshed;
                }
            }

            if (!_interactiveAllowed)
            {
                throw new McpLenseAuthException(
                    "OAuth token is missing, expired, or rejected and interactive flow is disabled " +
                    "(MCPLENSE_NO_INTERACTIVE_FLOW=1). Run with '--login' on a workstation with a browser, " +
                    "then retry the headless command.");
            }

            var fresh = await _orchestrator
                .RunInteractiveAsync(_auth, _resourceUri, cancellationToken)
                .ConfigureAwait(false);

            _current = fresh;
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ApplyBearer(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Produces a deep-enough clone of <paramref name="source"/> so we can re-send after a 401.
    /// Buffers any non-null content into memory; the original instance is not consumed by this call,
    /// but it has already been sent once so the framework's stream may have been disposed — buffering
    /// it once via <see cref="HttpContent.LoadIntoBufferAsync()"/> is safe regardless.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (source.Content is not null)
        {
            await source.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            var bytes = await source.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var header in source.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = content;
        }

        var sourceOptions = (IDictionary<string, object?>)source.Options;
        var cloneOptions = (IDictionary<string, object?>)clone.Options;
        foreach (var option in sourceOptions)
        {
            cloneOptions[option.Key] = option.Value;
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
            _ownedResource?.Dispose();
        }

        base.Dispose(disposing);
    }
}
