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
/// The inspector covers <see cref="AuthKind.InteractiveBrowser"/>, <see cref="AuthKind.OAuth"/>,
/// and <see cref="AuthKind.AzureCli"/>. For <c>azure-cli</c> the check is "is the user signed
/// in to <c>az</c>?" via a presence test on <c>~/.azure/azureProfile.json</c>, cached for the
/// lifetime of the inspector instance so multiple azure-cli profiles share one filesystem stat.
/// Other kinds (<c>Bearer</c>, <c>None</c>) are always considered "cached" because they have no
/// caching layer.
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
    private readonly Func<bool> _azureCliSignedInProbe;
    private bool? _azureCliSignedInCached;

    public MsalCacheInspector()
        : this(ProbeAzureCliSession)
    {
    }

    /// <summary>For tests: inject a fake azure-cli session probe.</summary>
    internal MsalCacheInspector(Func<bool> azureCliSignedInProbe)
    {
        _azureCliSignedInProbe = azureCliSignedInProbe ?? throw new ArgumentNullException(nameof(azureCliSignedInProbe));
    }

    public async Task<bool> HasCachedAccountAsync(AuthProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Auth.Kind switch
        {
            AuthKind.InteractiveBrowser => await HasInteractiveBrowserAccountAsync(profile.Auth, cancellationToken).ConfigureAwait(false),
            AuthKind.OAuth => await HasOAuthCachedTokenAsync(profile.Auth, cancellationToken).ConfigureAwait(false),
            AuthKind.AzureCli => IsAzureCliSignedIn(),
            // Bearer / None: no cache layer, always treat as "ready" to avoid spurious errors.
            _ => true
        };
    }

    /// <summary>
    /// Returns true when the Azure CLI appears to have an active session. Cached on first call
    /// so multiple azure-cli profiles in the same resolver invocation share one filesystem stat.
    /// Thread-safety is not enforced because the resolver invokes this sequentially per profile.
    /// </summary>
    private bool IsAzureCliSignedIn()
    {
        _azureCliSignedInCached ??= _azureCliSignedInProbe();
        return _azureCliSignedInCached.Value;
    }

    /// <summary>
    /// Heuristic: presence of <c>~/.azure/azureProfile.json</c> with non-empty
    /// <c>subscriptions</c> means <c>az login</c> has been run. We don't validate that the
    /// current default subscription is the one the profile wants; that's the runtime's job and
    /// surfaces as an authoritative error from <c>az</c> itself if there's a mismatch.
    /// </summary>
    private static bool ProbeAzureCliSession()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(userProfile))
            {
                return false;
            }

            var profilePath = Path.Combine(userProfile, ".azure", "azureProfile.json");
            if (!File.Exists(profilePath))
            {
                return false;
            }

            // A logged-in profile contains "subscriptions": [ { "id": "...", ... }, ... ].
            // A logged-out profile (after `az logout`) keeps the file but has "subscriptions": [].
            // Substring check is robust enough; full JSON parsing would catch a hand-edited file
            // but that's a corner case where any heuristic loses.
            var text = File.ReadAllText(profilePath);
            return text.Contains("\"subscriptions\"", StringComparison.Ordinal)
                   && text.Contains("\"id\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
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
