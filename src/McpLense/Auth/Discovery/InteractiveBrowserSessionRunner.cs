using Azure.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace McpLense;

/// <summary>
/// Drives the top-level <c>--login</c>/<c>--logout</c> short-circuit paths for servers configured
/// with <see cref="AuthKind.InteractiveBrowser"/>. Builds an
/// <see cref="Azure.Identity.InteractiveBrowserCredential"/> using the same composition as
/// <see cref="AuthHandlerFactory.BuildInteractiveBrowserCredential"/> so cache layout matches the
/// runtime path exactly.
/// </summary>
internal static class InteractiveBrowserSessionRunner
{
    public static async Task<AuthSessionReport> LoginAsync(IReadOnlyList<ResolvedServer> servers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var entries = new List<AuthSessionEntry>(servers.Count);
        foreach (var server in servers)
        {
            entries.Add(await LoginOneAsync(server, cancellationToken).ConfigureAwait(false));
        }

        return new AuthSessionReport("login", DateTimeOffset.UtcNow, entries);
    }

    public static async Task<AuthSessionReport> LogoutAsync(IReadOnlyList<ResolvedServer> servers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var entries = new List<AuthSessionEntry>(servers.Count);
        foreach (var server in servers)
        {
            entries.Add(await LogoutOneAsync(server, cancellationToken).ConfigureAwait(false));
        }

        return new AuthSessionReport("logout", DateTimeOffset.UtcNow, entries);
    }

    private static async Task<AuthSessionEntry> LoginOneAsync(ResolvedServer server, CancellationToken cancellationToken)
    {
        var validation = ValidateServer(server, "login");
        if (validation is not null)
        {
            return validation;
        }

        var auth = server.Auth!;

        try
        {
            var credential = AuthHandlerFactory.BuildInteractiveBrowserCredential(auth);
            var context = new TokenRequestContext(auth.Scopes!.ToArray());

            // AuthenticateAsync triggers the interactive flow up-front and primes the MSAL cache.
            // It returns an AuthenticationRecord; we do not need to persist it ourselves because
            // TokenCachePersistenceOptions already writes to the OS-protected on-disk cache.
            var record = await credential.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);

            var who = string.IsNullOrEmpty(record.Username) ? record.HomeAccountId : record.Username;
            var detail = $"signed in as '{who}'";
            return new AuthSessionEntry(server.Name, server.Target, Success: true, Detail: detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AuthSessionEntry(
                server.Name,
                server.Target,
                Success: false,
                Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<AuthSessionEntry> LogoutOneAsync(ResolvedServer server, CancellationToken cancellationToken)
    {
        var validation = ValidateServer(server, "logout");
        if (validation is not null)
        {
            return validation;
        }

        var auth = server.Auth!;
        var cacheName = string.IsNullOrEmpty(auth.CacheName)
            ? AuthHandlerFactory.DefaultInteractiveBrowserCacheName
            : auth.CacheName;

        try
        {
            // Build a Microsoft.Identity.Client PCA matching the credential's clientId/tenantId and
            // hook up the same MSAL cache file that Azure.Identity uses. We then enumerate this
            // app's accounts and remove them, rather than wiping the whole cache file. This keeps
            // the logout scoped to the current clientId, which matters when users share a cache
            // name with other tools (e.g. mcp-proxy).
            var pcaBuilder = PublicClientApplicationBuilder.Create(auth.ClientId);
            if (!string.IsNullOrEmpty(auth.TenantId))
            {
                pcaBuilder = pcaBuilder.WithTenantId(auth.TenantId);
            }

            var pca = pcaBuilder.Build();

            var storageProps = new StorageCreationPropertiesBuilder(cacheName, MsalCacheHelper.UserRootDirectory).Build();
            var helper = await MsalCacheHelper.CreateAsync(storageProps).ConfigureAwait(false);
            helper.RegisterCache(pca.UserTokenCache);

            var accounts = (await pca.GetAccountsAsync().ConfigureAwait(false)).ToList();
            foreach (var account in accounts)
            {
                await pca.RemoveAsync(account).ConfigureAwait(false);
            }

            var detail = accounts.Count > 0
                ? $"removed {accounts.Count} account(s) from cache '{cacheName}'"
                : $"no cached accounts to remove from cache '{cacheName}'";
            return new AuthSessionEntry(server.Name, server.Target, Success: true, Detail: detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AuthSessionEntry(
                server.Name,
                server.Target,
                Success: false,
                Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns null when <paramref name="server"/> is a valid interactive-browser target.
    /// Otherwise returns a failure entry so a single misconfigured server in a multi-server
    /// config does not abort the action for the rest.
    /// </summary>
    private static AuthSessionEntry? ValidateServer(ResolvedServer server, string action)
    {
        if (server.Kind != ConnectionKind.Http || server.Url is null)
        {
            return new AuthSessionEntry(
                server.Name,
                server.Target,
                Success: false,
                Error: $"--{action} only applies to HTTP/SSE servers; '{server.Name}' is a stdio target.");
        }

        if (server.Auth is null || server.Auth.Kind != AuthKind.InteractiveBrowser)
        {
            return new AuthSessionEntry(
                server.Name,
                server.Target,
                Success: false,
                Error: $"--{action} requires interactive-browser authentication on '{server.Name}'.");
        }

        if (string.IsNullOrEmpty(server.Auth.ClientId))
        {
            return new AuthSessionEntry(
                server.Name,
                server.Target,
                Success: false,
                Error: $"--{action} requires 'auth.clientId' on '{server.Name}'.");
        }

        if (server.Auth.Scopes is null || server.Auth.Scopes.Count == 0)
        {
            return new AuthSessionEntry(
                server.Name,
                server.Target,
                Success: false,
                Error: $"--{action} requires at least one 'auth.scopes' entry on '{server.Name}'.");
        }

        return null;
    }
}
