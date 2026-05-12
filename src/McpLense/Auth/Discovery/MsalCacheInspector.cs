using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace McpLense;

/// <summary>
/// Checks whether a profile already has at least one account cached in its MSAL on-disk cache,
/// without triggering any interactive flow. Used by <see cref="AuthProfileResolver"/> to pick a
/// silent profile when multiple are loaded.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately checks for the <em>presence of an account</em>, not the validity of its
/// access token. A cached account whose refresh token has expired will still register here; the
/// silent token-acquisition that follows during <c>SendAsync</c> will correctly surface the
/// re-auth requirement. This trade-off keeps the inspector cheap (no network calls) and avoids
/// false negatives caused by transient network issues.
/// </para>
/// <para>
/// The inspector covers <see cref="AuthKind.InteractiveBrowser"/> and <see cref="AuthKind.OAuth"/>
/// (the latter via the existing <see cref="OAuthTokenCache"/>). Other kinds (<c>Bearer</c>,
/// <c>None</c>) are always considered "cached" because they have no caching layer.
/// </para>
/// </remarks>
internal interface IMsalCacheInspector
{
    /// <summary>
    /// Returns true when the profile has at least one cached identity that could plausibly
    /// service a silent token acquisition.
    /// </summary>
    Task<bool> HasCachedAccountAsync(AuthProfile profile, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IMsalCacheInspector"/> wired to MSAL on-disk caches.</summary>
internal sealed class MsalCacheInspector : IMsalCacheInspector
{
    public async Task<bool> HasCachedAccountAsync(AuthProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Auth.Kind switch
        {
            AuthKind.InteractiveBrowser => await HasInteractiveBrowserAccountAsync(profile.Auth, cancellationToken).ConfigureAwait(false),
            AuthKind.OAuth => await HasOAuthCachedTokenAsync(profile.Auth, cancellationToken).ConfigureAwait(false),
            // AzureCli delegates token caching to the `az` CLI itself; we have no on-disk cache
            // to peek at. Treat as "cached" so multi-profile auto-pick treats it as a viable
            // candidate when no other profile has cached credentials. (If `az login` hasn't
            // been run, the runtime path surfaces an authoritative error on the first request.)
            AuthKind.AzureCli => true,
            // Bearer / None: no cache layer, always treat as "ready" to avoid spurious errors.
            _ => true
        };
    }

    private static async Task<bool> HasInteractiveBrowserAccountAsync(ResolvedAuth auth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(auth.ClientId))
        {
            return false;
        }

        var cacheName = string.IsNullOrEmpty(auth.CacheName)
            ? AuthHandlerFactory.DefaultInteractiveBrowserCacheName
            : auth.CacheName;

        try
        {
            var pcaBuilder = PublicClientApplicationBuilder.Create(auth.ClientId);
            if (!string.IsNullOrEmpty(auth.TenantId))
            {
                pcaBuilder = pcaBuilder.WithTenantId(auth.TenantId);
            }

            var pca = pcaBuilder.Build();

            var storageProps = new StorageCreationPropertiesBuilder(cacheName, MsalCacheHelper.UserRootDirectory).Build();
            var helper = await MsalCacheHelper.CreateAsync(storageProps).ConfigureAwait(false);
            helper.RegisterCache(pca.UserTokenCache);

            cancellationToken.ThrowIfCancellationRequested();
            var accounts = await pca.GetAccountsAsync().ConfigureAwait(false);
            return accounts.Any();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cache-corruption or platform-specific failure shouldn't crash auto-pick; treat
            // the profile as "no cached account" so the caller surfaces the missing-cache error
            // path instead of a confusing inspection failure.
            return false;
        }
    }

    private static async Task<bool> HasOAuthCachedTokenAsync(ResolvedAuth auth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(auth.CacheName) && string.IsNullOrEmpty(auth.ResourceUri))
        {
            // Without a deterministic cache key we cannot peek; defer to the lazy runtime path.
            return false;
        }

        try
        {
            var cache = new OAuthTokenCache();
            var key = !string.IsNullOrEmpty(auth.CacheName)
                ? auth.CacheName!
                : auth.ResourceUri!;
            var entry = await cache.LoadAsync(key, cancellationToken).ConfigureAwait(false);
            return entry is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
