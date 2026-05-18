using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace McpLense;

internal static class McpExecutor
{
    public static async Task<ExecutionOutcome> ExecuteAsync(ParsedCommand command, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        // Top-level login/logout commands have their own dispatch path: they don't go through
        // TargetResolver (no stdio/HTTP target to open), they operate purely on profiles.
        if (command.Command is AppCommand.Login)
        {
            var loginReport = await DispatchProfileLoginAsync(command.Target, cancellationToken).ConfigureAwait(false);
            return new ExecutionOutcome(loginReport, loginReport.Servers.Any(entry => !entry.Success));
        }

        if (command.Command is AppCommand.Logout)
        {
            var logoutReport = await DispatchProfileLogoutAsync(command.Target, cancellationToken).ConfigureAwait(false);
            return new ExecutionOutcome(logoutReport, logoutReport.Servers.Any(entry => !entry.Success));
        }

        var servers = await TargetResolver.ResolveAsync(command.Target, cancellationToken);

        // Resolve auth profiles for HTTP servers that don't already have inline auth (i.e.
        // anything other than --auth bearer). --no-auth short-circuits this entirely so quick
        // local debugging works without any profile setup.
        if (!command.Target.AuthOverrides.NoAuth)
        {
            servers = await AttachProfilesAsync(servers, command.Target, cancellationToken).ConfigureAwait(false);
        }

        return command.Command switch
        {
            AppCommand.Inspect => await InspectAsync(servers, command.Timeout, cancellationToken),
            AppCommand.Tools => await ListToolsAsync(servers, command.Timeout, cancellationToken),
            AppCommand.Resources => await ListResourcesAsync(servers, command.Timeout, cancellationToken),
            AppCommand.Prompts => await ListPromptsAsync(servers, command.Timeout, cancellationToken),
            AppCommand.Call => await CallToolAsync(SingleServer(servers), command.Subject!, command.Arguments!, command.Timeout, command.ProgressEnabled, cancellationToken),
            AppCommand.Read => await ReadResourceAsync(SingleServer(servers), command.Subject!, command.Arguments, command.Timeout, cancellationToken),
            AppCommand.Prompt => await GetPromptAsync(SingleServer(servers), command.Subject!, command.Arguments!, command.Timeout, cancellationToken),
            _ => throw new UserInputException($"Unsupported command '{command.Command}'.")
        };
    }

    /// <summary>
    /// Resolves the set of profiles to act on for the top-level <c>mcplense login/logout</c>
    /// commands. Returns an ordered list of (profile, optional URL) pairs; the URL is only
    /// non-null when the user passed a positional URL.
    /// </summary>
    private static async Task<IReadOnlyList<(AuthProfile Profile, Uri? Url)>> ResolveProfilesForSessionAsync(
        TargetOptions target,
        CancellationToken cancellationToken)
    {
        var profilePaths = ResolveProfilePaths(target.ProfilePaths);
        var profiles = await ProfileLoader.LoadAsync(profilePaths, new EnvironmentExpander(), cancellationToken).ConfigureAwait(false);

        if (target.AuthOverrides.All)
        {
            if (profiles.Count == 0)
            {
                throw new UserInputException(
                    $"No auth profiles are loaded. Drop a profile file in '$XDG_CONFIG_HOME/McpLense/{DefaultConfigPaths.ProfilesFileName}' or pass one via --profiles <path>.");
            }

            return profiles.Select(p => (p, (Uri?)null)).ToArray();
        }

        if (!string.IsNullOrEmpty(target.AuthOverrides.Profile))
        {
            var match = profiles.FirstOrDefault(p => string.Equals(p.Name, target.AuthOverrides.Profile, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var available = profiles.Count == 0 ? "(none loaded)" : string.Join(", ", profiles.Select(p => p.Name));
                throw new UserInputException(
                    $"--profile '{target.AuthOverrides.Profile}' was not found. Loaded profiles: {available}.");
            }

            return new[] { (match, (Uri?)null) };
        }

        if (target.Url is not null)
        {
            using var probe = new AuthProbe();
            var resolver = new AuthProfileResolver(probe, new MsalCacheInspector());
            var profile = await resolver.ResolveAsync(target.Url, profiles, requestedProfile: null, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                throw new UserInputException($"No profile resolved for {target.Url}.");
            }

            return new[] { (profile, (Uri?)target.Url) };
        }

        // CommandLineParser guards this case, but defend at the executor edge.
        throw new UserInputException("login/logout requires --all, --profile <name>, or a positional URL.");
    }

    /// <summary>Top-level <c>mcplense login</c>: drives the auth flow for one or more profiles.</summary>
    private static async Task<AuthSessionReport> DispatchProfileLoginAsync(TargetOptions target, CancellationToken cancellationToken)
    {
        var pairs = await ResolveProfilesForSessionAsync(target, cancellationToken).ConfigureAwait(false);
        var entries = new List<AuthSessionEntry>(pairs.Count);
        foreach (var (profile, url) in pairs)
        {
            entries.Add(await LoginOneProfileAsync(profile, url, cancellationToken).ConfigureAwait(false));
        }

        return new AuthSessionReport("login", DateTimeOffset.UtcNow, entries);
    }

    /// <summary>Top-level <c>mcplense logout</c>: clears cached state for one or more profiles.</summary>
    private static async Task<AuthSessionReport> DispatchProfileLogoutAsync(TargetOptions target, CancellationToken cancellationToken)
    {
        var pairs = await ResolveProfilesForSessionAsync(target, cancellationToken).ConfigureAwait(false);
        var entries = new List<AuthSessionEntry>(pairs.Count);
        foreach (var (profile, url) in pairs)
        {
            entries.Add(await LogoutOneProfileAsync(profile, url, cancellationToken).ConfigureAwait(false));
        }

        return new AuthSessionReport("logout", DateTimeOffset.UtcNow, entries);
    }

    /// <summary>
    /// Wraps a single profile in a synthetic <see cref="ResolvedServer"/> so it can flow through
    /// the existing <see cref="InteractiveBrowserSessionRunner"/> / <see cref="AuthSessionRunner"/>
    /// implementations. The synthetic URL falls back to the profile's <c>resourceUri</c> (OAuth
    /// only) when no positional URL was supplied; interactive-browser and azure-cli profiles
    /// get a placeholder since the credential only needs clientId/tenantId/scopes.
    /// </summary>
    private static ResolvedServer? SynthesizeServer(AuthProfile profile, Uri? url)
    {
        var effectiveUrl = url
            ?? (Uri.TryCreate(profile.Auth.ResourceUri, UriKind.Absolute, out var resourceUri) ? resourceUri : null);

        if (effectiveUrl is null && (profile.Auth.Kind == AuthKind.InteractiveBrowser || profile.Auth.Kind == AuthKind.AzureCli))
        {
            effectiveUrl = new Uri($"https://profile.{profile.Name}.local/");
        }

        if (effectiveUrl is null)
        {
            return null;
        }

        return new ResolvedServer(
            Name: profile.Name,
            Kind: ConnectionKind.Http,
            Target: effectiveUrl.ToString(),
            Source: "profile",
            Command: null,
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: effectiveUrl,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Auth: profile.Auth);
    }

    private static async Task<AuthSessionEntry> LoginOneProfileAsync(AuthProfile profile, Uri? url, CancellationToken cancellationToken)
    {
        var server = SynthesizeServer(profile, url);
        if (server is null)
        {
            return new AuthSessionEntry(
                profile.Name,
                Target: $"profile:{profile.Name}",
                Success: false,
                Error: $"OAuth profile '{profile.Name}' has no 'resourceUri'; pass a positional URL or set 'auth.resourceUri' in the profile.");
        }

        return profile.Auth.Kind switch
        {
            AuthKind.InteractiveBrowser => (await InteractiveBrowserSessionRunner.LoginAsync([server], cancellationToken).ConfigureAwait(false)).Servers[0],
            AuthKind.OAuth => (await AuthSessionRunner.LoginAsync([server], cancellationToken).ConfigureAwait(false)).Servers[0],
            AuthKind.AzureCli => await LoginAzureCliAsync(profile, server, cancellationToken).ConfigureAwait(false),
            AuthKind.Bearer => new AuthSessionEntry(profile.Name, server.Target, Success: true, Detail: "bearer profiles need no login"),
            _ => new AuthSessionEntry(profile.Name, server.Target, Success: false, Error: $"unsupported auth kind {profile.Auth.Kind}")
        };
    }

    private static async Task<AuthSessionEntry> LogoutOneProfileAsync(AuthProfile profile, Uri? url, CancellationToken cancellationToken)
    {
        var server = SynthesizeServer(profile, url);
        if (server is null)
        {
            return new AuthSessionEntry(
                profile.Name,
                Target: $"profile:{profile.Name}",
                Success: false,
                Error: $"OAuth profile '{profile.Name}' has no 'resourceUri'; pass a positional URL or set 'auth.resourceUri' in the profile.");
        }

        return profile.Auth.Kind switch
        {
            AuthKind.InteractiveBrowser => (await InteractiveBrowserSessionRunner.LogoutAsync([server], cancellationToken).ConfigureAwait(false)).Servers[0],
            AuthKind.OAuth => (await AuthSessionRunner.LogoutAsync([server], cancellationToken).ConfigureAwait(false)).Servers[0],
            AuthKind.AzureCli => new AuthSessionEntry(
                profile.Name,
                server.Target,
                Success: true,
                Detail: "azure-cli profiles delegate to the Azure CLI; run 'az logout' to clear that session"),
            AuthKind.Bearer => new AuthSessionEntry(profile.Name, server.Target, Success: true, Detail: "bearer profiles have no cache to clear"),
            _ => new AuthSessionEntry(profile.Name, server.Target, Success: false, Error: $"unsupported auth kind {profile.Auth.Kind}")
        };
    }

    /// <summary>
    /// Verifies an azure-cli profile by asking <see cref="Azure.Identity.AzureCliCredential"/>
    /// for a token against the profile's scopes. Success means <c>az</c> is installed, the user
    /// is logged in, and Entra issued a token for the requested resource. There is no MSAL cache
    /// to prime - the CLI keeps its own session - but proving "token acquisition works right now"
    /// is the most useful thing <c>mcplense login</c> can do for this auth kind.
    /// </summary>
    private static async Task<AuthSessionEntry> LoginAzureCliAsync(AuthProfile profile, ResolvedServer server, CancellationToken cancellationToken)
    {
        try
        {
            var credential = AuthHandlerFactory.BuildAzureCliCredential(profile.Auth);
            var context = new Azure.Core.TokenRequestContext(profile.Auth.Scopes!.ToArray());
            var token = await credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);

            var detail = token.ExpiresOn > DateTimeOffset.MinValue
                ? $"acquired token via 'az' (expires {token.ExpiresOn:O})"
                : "acquired token via 'az'";
            return new AuthSessionEntry(profile.Name, server.Target, Success: true, Detail: detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AuthSessionEntry(
                profile.Name,
                server.Target,
                Success: false,
                Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Attaches a resolved <see cref="AuthProfile"/>'s auth block onto every HTTP server that
    /// doesn't already have inline auth (e.g. set via <c>--auth bearer</c> in
    /// <see cref="TargetResolver.ApplyAuthOverrides"/>). Stdio servers are left alone.
    ///
    /// Profile attachment is conditional:
    /// <list type="bullet">
    ///   <item>If <c>--profile</c> is set explicitly → attach the named profile (error if missing).</item>
    ///   <item>If profiles are loaded (explicitly via <c>--profiles</c> OR auto-discovered from
    ///   the XDG default location) → probe the server URL; attach only when the probe says auth
    ///   is required (RFC 9728 metadata present, or the server emitted a <c>401</c>).</item>
    ///   <item>Otherwise → leave <c>Auth</c> at <c>null</c> (plain connection; the server will
    ///   surface a 401 if needed, which the user can resolve by adding a profile).</item>
    /// </list>
    /// Profiles come from the merged set of <c>--profiles</c> paths plus the XDG defaults
    /// (<see cref="DefaultConfigPaths"/>); duplicates across files raise a
    /// <see cref="UserInputException"/>.
    /// </summary>
    private static async Task<IReadOnlyList<ResolvedServer>> AttachProfilesAsync(
        IReadOnlyList<ResolvedServer> servers,
        TargetOptions target,
        CancellationToken cancellationToken)
    {
        var httpWithoutAuth = servers
            .Where(server => server.Kind == ConnectionKind.Http && server.Auth is null)
            .ToList();

        if (httpWithoutAuth.Count == 0)
        {
            return servers;
        }

        var explicitProfile = target.AuthOverrides.Profile;
        var profilePaths = ResolveProfilePaths(target.ProfilePaths);

        // No profiles, no --profile, no --try-all: skip the entire dance and let the runtime
        // attempt a plain connection. This preserves "just hit the URL" UX for servers that
        // don't need auth at all (the most common dev/test case).
        if (string.IsNullOrEmpty(explicitProfile) && !target.AuthOverrides.TryAll && profilePaths.Count == 0)
        {
            return servers;
        }

        var profiles = await ProfileLoader.LoadAsync(profilePaths, new EnvironmentExpander(), cancellationToken).ConfigureAwait(false);

        // --try-all is a runtime-only opt-in for now; runtime command paths still need a single
        // profile choice. Surface a clean error rather than silently picking one.
        if (target.AuthOverrides.TryAll)
        {
            throw new UserInputException(
                "--try-all is currently only supported with --login. Pick a profile with --profile <name> for runtime commands.");
        }

        using var probe = new AuthProbe();
        var resolver = new AuthProfileResolver(probe, new MsalCacheInspector());

        var result = new ResolvedServer[servers.Count];
        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];
            if (server.Kind != ConnectionKind.Http || server.Auth is not null)
            {
                result[index] = server;
                continue;
            }

            // Single source of truth: the resolver decides. It short-circuits the single-profile
            // case (no probe), and only probes when disambiguating among multiple profiles. This
            // replaces an earlier pre-probe in this method that caused two HTTP round-trips per
            // server resolution and surfaced as 30+ second hangs against slow / flaky servers
            // (e.g. Agent365 returning 502/timeouts on unauthenticated HEAD).
            var profile = await resolver.ResolveAsync(server.Url!, profiles, explicitProfile, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                result[index] = server;
                continue;
            }

            var auth = await MaybeSubstituteScopesFromProbeAsync(profile.Auth, server.Url!, probe, cancellationToken).ConfigureAwait(false);
            result[index] = server with { Auth = auth };
        }

        return result;
    }

    /// <summary>
    /// When a profile's scopes are all <c>&lt;resource&gt;/.default</c> style, replace them with
    /// what the server's RFC 9728 Protected Resource Metadata document advertises. This is what
    /// makes one Entra profile work against servers (like Agent365) that namespace scopes
    /// per-MCP-server-URL rather than per-app-id.
    ///
    /// Heuristic: if EVERY scope on the profile ends with <c>/.default</c>, we assume the user's
    /// intent is "give me whatever this resource expects" and substitute. Profiles with explicit
    /// permission names (<c>mcp.read</c>, <c>repo</c>, etc.) are left untouched - those users
    /// know exactly what they're asking for.
    ///
    /// Substitution preference order (first non-empty wins):
    /// <list type="number">
    ///   <item><description>Specific advertised scopes (e.g. <c>"McpServers.Mail.All"</c>) other
    ///   than standard OIDC names. Bare names get fully-qualified using the metadata's
    ///   <c>resource</c> field (or the server URL when the metadata omits one). This is preferred
    ///   over <c>.default</c> because Entra's <c>.default</c> only emits statically
    ///   pre-consented permissions, so a token request with <c>.default</c> against a resource
    ///   the calling client has never consented to comes back without the needed scope claims.
    ///   Asking for the specific scope makes Entra include it (and triggers dynamic consent for
    ///   interactive flows).</description></item>
    ///   <item><description>Advertised <c>.default</c> forms. Useful when the server only
    ///   advertises <c>&lt;resource&gt;/.default</c> (e.g. Agent365). The token still depends on
    ///   prior consent, but at least the audience targets the correct resource URI.</description></item>
    ///   <item><description>The profile's original scopes, unchanged.</description></item>
    /// </list>
    ///
    /// Standard OIDC scopes (<c>openid</c>, <c>profile</c>, <c>offline_access</c>, etc.) are
    /// excluded from the "specific" set because they're orthogonal to a resource's permission
    /// model and would never satisfy "what does this server want".
    ///
    /// The probe is shared (memoised) with the resolver via <see cref="AuthProbe"/>'s per-URL
    /// cache, so this method costs zero round-trips when the resolver already probed.
    /// </summary>
    internal static async Task<ResolvedAuth> MaybeSubstituteScopesFromProbeAsync(
        ResolvedAuth auth,
        Uri serverUrl,
        IAuthProbe probe,
        CancellationToken cancellationToken)
    {
        if (!AllScopesAreDefault(auth.Scopes))
        {
            return auth;
        }

        var probeResult = await probe.ProbeAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        if (probeResult.Scopes is null || probeResult.Scopes.Count == 0)
        {
            return auth;
        }

        var resourceBase = string.IsNullOrEmpty(probeResult.Resource) ? serverUrl.ToString() : probeResult.Resource;

        // Pass 1: specific (non-.default, non-OIDC) advertised scopes win.
        var specific = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in probeResult.Scopes)
        {
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            if (raw.EndsWith("/.default", StringComparison.Ordinal))
            {
                continue;
            }

            if (OidcStandardScopes.Contains(raw))
            {
                continue;
            }

            var qualified = QualifyScope(raw, resourceBase);
            if (qualified is null)
            {
                continue;
            }

            if (seen.Add(qualified))
            {
                specific.Add(qualified);
            }
        }

        if (specific.Count > 0)
        {
            return auth with { Scopes = specific };
        }

        // Pass 2: advertised .default forms (preserves prior Agent365 behaviour).
        var advertisedDefault = probeResult.Scopes
            .Where(s => s.EndsWith("/.default", StringComparison.Ordinal))
            .ToArray();
        if (advertisedDefault.Length > 0)
        {
            return auth with { Scopes = advertisedDefault };
        }

        return auth;
    }

    /// <summary>
    /// Standard OIDC scopes (RFC 6749 + OIDC Core). Excluded from the "specific advertised
    /// scopes" substitution because they describe identity-token claims, not resource-server
    /// permissions, and would never authorise a tools call by themselves.
    /// </summary>
    private static readonly HashSet<string> OidcStandardScopes = new(StringComparer.Ordinal)
    {
        "openid",
        "profile",
        "offline_access",
        "email",
        "groups",
        "roles",
        "address",
        "phone"
    };

    /// <summary>
    /// Returns a fully-qualified scope string. Already-FQN scopes (containing <c>://</c>) pass
    /// through untouched. Bare names (<c>"User.Read.All"</c>) are prefixed with
    /// <paramref name="resourceBase"/> so the auth server can resolve them to the correct
    /// resource (e.g. <c>"https://api.example/User.Read.All"</c>). Returns <c>null</c> when the
    /// scope is bare and no resource base is available (the caller drops it).
    /// </summary>
    private static string? QualifyScope(string scope, string? resourceBase)
    {
        if (scope.Contains("://", StringComparison.Ordinal))
        {
            return scope;
        }

        if (string.IsNullOrEmpty(resourceBase))
        {
            return null;
        }

        return $"{resourceBase.TrimEnd('/')}/{scope}";
    }

    private static bool AllScopesAreDefault(IReadOnlyList<string>? scopes)
    {
        if (scopes is null || scopes.Count == 0)
        {
            return false;
        }

        return scopes.All(s => s.EndsWith("/.default", StringComparison.Ordinal));
    }

    /// <summary>
    /// Combines the user-supplied <c>--profiles</c> paths with the XDG default search results.
    /// Returns an empty list when nothing is found; the resolver surfaces a helpful error in
    /// that case.
    /// </summary>
    private static IReadOnlyList<string> ResolveProfilePaths(IReadOnlyList<string> explicitPaths)
    {
        if (explicitPaths.Count > 0)
        {
            return explicitPaths;
        }

        var root = DefaultConfigPaths.ResolveRoot();
        return DefaultConfigPaths.EnumerateProfileFiles(root);
    }

    private static ResolvedServer SingleServer(IReadOnlyList<ResolvedServer> servers)
        => servers.Count switch
        {
            1 => servers[0],
            0 => throw new UserInputException("No server was resolved."),
            _ => throw new UserInputException("This command requires exactly one server. Use --server with --config to select one.")
        };

    private static async Task<ExecutionOutcome> InspectAsync(IReadOnlyList<ResolvedServer> servers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(server => InspectServerAsync(server, timeout, cancellationToken)));
        var hasErrors = results.Any(HasInspectErrors);
        return new ExecutionOutcome(new InspectReport(DateTimeOffset.UtcNow, results), hasErrors);
    }

    private static async Task<ExecutionOutcome> ListToolsAsync(IReadOnlyList<ResolvedServer> servers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(server => WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var items = await LoadToolsAsync(client, ct);
            return new ServerItems<ToolInfo>(server.Name, FormatTransport(server.Kind), server.Target, items);
        })));

        return new ExecutionOutcome(new ToolListReport(DateTimeOffset.UtcNow, servers.Zip(results, ToServerItems).ToArray()), results.Any(result => result.Error is not null));
    }

    private static async Task<ExecutionOutcome> ListResourcesAsync(IReadOnlyList<ResolvedServer> servers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(server => WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var items = await LoadResourcesAsync(client, ct);
            return new ServerItems<ResourceInfo>(server.Name, FormatTransport(server.Kind), server.Target, items);
        })));

        return new ExecutionOutcome(new ResourceListReport(DateTimeOffset.UtcNow, servers.Zip(results, ToServerItems).ToArray()), results.Any(result => result.Error is not null));
    }

    private static async Task<ExecutionOutcome> ListPromptsAsync(IReadOnlyList<ResolvedServer> servers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(server => WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var items = await LoadPromptsAsync(client, ct);
            return new ServerItems<PromptInfo>(server.Name, FormatTransport(server.Kind), server.Target, items);
        })));

        return new ExecutionOutcome(new PromptListReport(DateTimeOffset.UtcNow, servers.Zip(results, ToServerItems).ToArray()), results.Any(result => result.Error is not null));
    }

    private static async Task<ExecutionOutcome> CallToolAsync(ResolvedServer server, string toolName, JsonObject arguments, TimeSpan timeout, bool progressEnabled, CancellationToken cancellationToken)
    {
        var progressUpdates = new List<ProgressUpdate>();
        var progress = progressEnabled ? new Progress<ProgressNotificationValue>(value =>
        {
            var update = new ProgressUpdate(value.Progress, value.Total, value.Message, DateTimeOffset.UtcNow);
            progressUpdates.Add(update);
            WriteProgress(server, toolName, update);
        }) : null;

        var result = await WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var response = await client.CallToolAsync(toolName, ToDictionary(arguments), progress: progress, options: null, cancellationToken: ct);
            return new ToolCallReport(DateTimeOffset.UtcNow, ToReference(server), toolName, arguments, progressUpdates.ToArray(), MapCallResult(response));
        });

        return new ExecutionOutcome(result.Value ?? new ToolCallReport(DateTimeOffset.UtcNow, ToReference(server), toolName, arguments, progressUpdates.ToArray(), null, result.Error), result.Error is not null || result.Value?.Result?.IsError == true);
    }

    private static async Task<ExecutionOutcome> ReadResourceAsync(ResolvedServer server, string resource, JsonObject? arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            object response = arguments is null
                ? await client.ReadResourceAsync(resource, options: null, cancellationToken: ct)
                : await client.ReadResourceAsync(resource, ToDictionary(arguments), options: null, cancellationToken: ct);

            return new ReadReport(DateTimeOffset.UtcNow, ToReference(server), resource, arguments, MapReadResult(response));
        });

        return new ExecutionOutcome(result.Value ?? new ReadReport(DateTimeOffset.UtcNow, ToReference(server), resource, arguments, null, result.Error), result.Error is not null);
    }

    private static async Task<ExecutionOutcome> GetPromptAsync(ResolvedServer server, string promptName, JsonObject arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var response = await client.GetPromptAsync(promptName, ToDictionary(arguments), options: null, cancellationToken: ct);
            return new PromptCallReport(DateTimeOffset.UtcNow, ToReference(server), promptName, arguments, MapPromptResult(response));
        });

        return new ExecutionOutcome(result.Value ?? new PromptCallReport(DateTimeOffset.UtcNow, ToReference(server), promptName, arguments, null, result.Error), result.Error is not null);
    }

    private static async Task<ServerInspection> InspectServerAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await WithClientAsync(server, timeout, cancellationToken, async (client, ct) =>
        {
            var capabilities = GetCapabilities(client);
            var tools = await TrySectionAsync(capabilities.Tools, () => LoadToolsAsync(client, ct));
            var resources = await TrySectionAsync(capabilities.Resources, () => LoadResourcesAsync(client, ct));
            var templates = await TrySectionAsync(capabilities.Resources, () => LoadResourceTemplatesAsync(client, ct));
            var prompts = await TrySectionAsync(capabilities.Prompts, () => LoadPromptsAsync(client, ct));

            return new ServerInspection(server.Name, FormatTransport(server.Kind), server.Target, capabilities, tools, resources, templates, prompts);
        });

        return result.Value ?? new ServerInspection(
            server.Name,
            FormatTransport(server.Kind),
            server.Target,
            new CapabilitySnapshot(false, false, false, false, false),
            new SectionResult<ToolInfo>(false, []),
            new SectionResult<ResourceInfo>(false, []),
            new SectionResult<ResourceTemplateInfo>(false, []),
            new SectionResult<PromptInfo>(false, []),
            result.Error);
    }

    private static bool HasInspectErrors(ServerInspection inspection)
        => inspection.Error is not null
           || inspection.Tools.Error is not null
           || inspection.Resources.Error is not null
           || inspection.ResourceTemplates.Error is not null
           || inspection.Prompts.Error is not null;

    private static async Task<SectionResult<T>> TrySectionAsync<T>(bool supported, Func<Task<IReadOnlyList<T>>> loader)
    {
        if (!supported)
        {
            return new SectionResult<T>(false, []);
        }

        try
        {
            return new SectionResult<T>(true, await loader());
        }
        catch (Exception ex)
        {
            return new SectionResult<T>(true, [], FormatException(ex));
        }
    }

    private static async Task<IReadOnlyList<ToolInfo>> LoadToolsAsync(McpClient client, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Cast<object>().Select(MapTool).ToArray();
    }

    private static async Task<IReadOnlyList<ResourceInfo>> LoadResourcesAsync(McpClient client, CancellationToken cancellationToken)
    {
        var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken);
        return resources.Cast<object>().Select(MapResource).ToArray();
    }

    private static async Task<IReadOnlyList<ResourceTemplateInfo>> LoadResourceTemplatesAsync(McpClient client, CancellationToken cancellationToken)
    {
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: cancellationToken);
        return templates.Cast<object>().Select(MapResourceTemplate).ToArray();
    }

    private static async Task<IReadOnlyList<PromptInfo>> LoadPromptsAsync(McpClient client, CancellationToken cancellationToken)
    {
        var prompts = await client.ListPromptsAsync(cancellationToken: cancellationToken);
        return prompts.Cast<object>().Select(MapPrompt).ToArray();
    }

    private static async Task<OperationResult<T>> WithClientAsync<T>(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken, Func<McpClient, CancellationToken, Task<T>> operation)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await using var client = await CreateClientAsync(server, timeout, timeoutSource.Token);
            return new OperationResult<T>(await operation(client, timeoutSource.Token), null);
        }
        catch (Exception ex)
        {
            return new OperationResult<T>(default, FormatException(ex));
        }
    }

    private static async Task<McpClient> CreateClientAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (server.Kind is ConnectionKind.Http)
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = server.Url!,
                Name = server.Name,
                TransportMode = ToHttpTransportMode(server.Transport),
                ConnectionTimeout = timeout
            };

            if (server.Headers.Count > 0)
            {
                SetProperty(options, server.Headers.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase), "AdditionalHeaders");
            }

            if (server.Auth is { Kind: not AuthKind.None })
            {
                var authHandler = AuthHandlerFactory.Create(server.Auth, server.Url);
                if (authHandler is not null)
                {
                    authHandler.InnerHandler = new SocketsHttpHandler();
                    var http = new HttpClient(authHandler, disposeHandler: true);
                    return await McpClient.CreateAsync(new HttpClientTransport(options, http, ownsHttpClient: true), cancellationToken: cancellationToken);
                }
            }

            return await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: cancellationToken);
        }

        var stdioOptions = new StdioClientTransportOptions
        {
            Command = server.Command!,
            Name = server.Name,
            Arguments = [.. server.CommandArguments],
            ShutdownTimeout = timeout
        };

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            stdioOptions.WorkingDirectory = server.WorkingDirectory;
        }

        if (server.Environment.Count > 0)
        {
            SetProperty(stdioOptions, server.Environment.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase), "EnvironmentVariables");
        }

        return await McpClient.CreateAsync(new StdioClientTransport(stdioOptions), cancellationToken: cancellationToken);
    }

    private static CapabilitySnapshot GetCapabilities(McpClient client)
    {
        var capabilities = GetPropertyValue(client, "ServerCapabilities");
        return new CapabilitySnapshot(
            Tools: GetPropertyValue(capabilities, "Tools") is not null,
            Resources: GetPropertyValue(capabilities, "Resources") is not null,
            Prompts: GetPropertyValue(capabilities, "Prompts") is not null,
            Logging: GetPropertyValue(capabilities, "Logging") is not null,
            Completions: GetPropertyValue(capabilities, "Completions") is not null);
    }

    private static ToolInfo MapTool(object tool)
        => new(
            Name: GetStringProperty(tool, "Name") ?? string.Empty,
            Description: GetStringProperty(tool, "Description"),
            InputSchema: ToJsonNode(GetPropertyValue(tool, "ProtocolTool", "InputSchema") ?? GetPropertyValue(tool, "InputSchema")));

    private static ResourceInfo MapResource(object resource)
        => new(
            Name: GetStringProperty(resource, "Name"),
            Uri: GetStringProperty(resource, "Uri"),
            MimeType: GetStringProperty(resource, "MimeType"),
            Description: GetStringProperty(resource, "Description"));

    private static ResourceTemplateInfo MapResourceTemplate(object template)
        => new(
            Name: GetStringProperty(template, "Name"),
            UriTemplate: GetStringProperty(template, "UriTemplate"),
            MimeType: GetStringProperty(template, "MimeType"),
            Description: GetStringProperty(template, "Description"));

    private static PromptInfo MapPrompt(object prompt)
    {
        var arguments = EnumerateObjects(GetPropertyValue(prompt, "ProtocolPrompt", "Arguments") ?? GetPropertyValue(prompt, "Arguments"))
            .Select(argument => new PromptArgumentInfo(
                Name: GetStringProperty(argument, "Name"),
                Description: GetStringProperty(argument, "Description"),
                Required: GetBoolProperty(argument, "Required") ?? false))
            .ToArray();

        return new PromptInfo(
            Name: GetStringProperty(prompt, "Name") ?? string.Empty,
            Description: GetStringProperty(prompt, "Description"),
            Arguments: arguments);
    }

    private static CallResultView MapCallResult(object result)
        => new(
            IsError: GetBoolProperty(result, "IsError"),
            StructuredContent: ToJsonNode(GetPropertyValue(result, "StructuredContent")),
            Meta: ToJsonNode(GetPropertyValue(result, "Meta") ?? GetPropertyValue(result, "_meta")),
            Content: EnumerateObjects(GetPropertyValue(result, "Content")).Select(MapContentBlock).ToArray());

    private static ReadResourceView MapReadResult(object result)
        => new(EnumerateObjects(GetPropertyValue(result, "Contents")).Select(MapResourceContent).ToArray());

    private static PromptResultView MapPromptResult(object result)
        => new(
            Description: GetStringProperty(result, "Description"),
            Messages: EnumerateObjects(GetPropertyValue(result, "Messages")).Select(MapPromptMessage).ToArray());

    private static PromptMessageView MapPromptMessage(object message)
        => new(
            Role: GetPropertyValue(message, "Role")?.ToString(),
            Content: GetPropertyValue(message, "Content") is { } content ? MapContentBlock(content) : null);

    private static ContentBlockView MapContentBlock(object block)
    {
        var typeName = block.GetType().Name;

        return typeName switch
        {
            "TextContentBlock" => new ContentBlockView(
                Kind: "text",
                Text: GetStringProperty(block, "Text"),
                MimeType: GetStringProperty(block, "MimeType")),
            "ImageContentBlock" => CreateBinaryContent("image", block),
            "AudioContentBlock" => CreateBinaryContent("audio", block),
            "EmbeddedResourceBlock" => new ContentBlockView(
                Kind: "resource",
                Resource: GetPropertyValue(block, "Resource") is { } resource ? MapResourceContent(resource) : null),
            _ => new ContentBlockView(typeName, Raw: ToJsonNode(block))
        };
    }

    private static ContentBlockView CreateBinaryContent(string kind, object block)
    {
        var bytes = TryGetBytes(GetPropertyValue(block, "DecodedData") ?? GetPropertyValue(block, "Data"));
        return new ContentBlockView(
            Kind: kind,
            MimeType: GetStringProperty(block, "MimeType"),
            DataBase64: bytes is null ? null : Convert.ToBase64String(bytes),
            ByteCount: bytes?.Length);
    }

    private static ResourceContentView MapResourceContent(object content)
    {
        var typeName = content.GetType().Name;
        return typeName switch
        {
            "TextResourceContents" => new ResourceContentView(
                Kind: "text",
                Uri: GetStringProperty(content, "Uri"),
                MimeType: GetStringProperty(content, "MimeType"),
                Text: GetStringProperty(content, "Text")),
            "BlobResourceContents" => CreateBlobResourceContent(content),
            _ => new ResourceContentView(typeName, Raw: ToJsonNode(content))
        };
    }

    private static ResourceContentView CreateBlobResourceContent(object content)
    {
        var bytes = TryGetBytes(GetPropertyValue(content, "Blob") ?? GetPropertyValue(content, "Data"));
        return new ResourceContentView(
            Kind: "blob",
            Uri: GetStringProperty(content, "Uri"),
            MimeType: GetStringProperty(content, "MimeType"),
            DataBase64: bytes is null ? null : Convert.ToBase64String(bytes),
            ByteCount: bytes?.Length);
    }

    private static ServerReference ToReference(ResolvedServer server)
        => new(server.Name, FormatTransport(server.Kind), server.Target);

    private static ServerItems<T> ToServerItems<T>(ResolvedServer server, OperationResult<ServerItems<T>> result)
        => result.Value ?? new ServerItems<T>(server.Name, FormatTransport(server.Kind), server.Target, [], result.Error);

    private static string FormatTransport(ConnectionKind kind)
        => kind is ConnectionKind.Http ? "http" : "stdio";

    private static HttpTransportMode ToHttpTransportMode(TransportPreference preference) => preference switch
    {
        TransportPreference.Auto => HttpTransportMode.AutoDetect,
        TransportPreference.StreamableHttp => HttpTransportMode.StreamableHttp,
        TransportPreference.Sse => HttpTransportMode.Sse,
        _ => HttpTransportMode.AutoDetect
    };

    private static string FormatException(Exception exception)
        => exception is OperationCanceledException ? "Timed out." : $"{exception.GetType().Name}: {exception.Message}";

    private static void WriteProgress(ResolvedServer server, string toolName, ProgressUpdate update)
    {
        var pieces = new List<string> { $"[{server.Name}] {toolName}" };

        if (update.Progress is not null && update.Total is not null)
        {
            pieces.Add($"{update.Progress:0.##}/{update.Total:0.##}");
        }
        else if (update.Progress is not null)
        {
            pieces.Add(update.Progress.Value <= 100 ? $"{update.Progress:0.##}%" : update.Progress.Value.ToString("0.##"));
        }

        if (!string.IsNullOrWhiteSpace(update.Message))
        {
            pieces.Add(update.Message!);
        }

        Console.Error.WriteLine(string.Join(" | ", pieces));
    }

    private static Dictionary<string, object?> ToDictionary(JsonObject obj)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in obj)
        {
            values[entry.Key] = ToClrValue(entry.Value);
        }

        return values;
    }

    private static object? ToClrValue(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject obj => obj.ToDictionary(static pair => pair.Key, static pair => ToClrValue(pair.Value), StringComparer.OrdinalIgnoreCase),
            JsonArray array => array.Select(ToClrValue).ToList(),
            JsonValue value when value.TryGetValue<bool>(out var boolValue) => boolValue,
            JsonValue value when value.TryGetValue<int>(out var intValue) => intValue,
            JsonValue value when value.TryGetValue<long>(out var longValue) => longValue,
            JsonValue value when value.TryGetValue<decimal>(out var decimalValue) => decimalValue,
            JsonValue value when value.TryGetValue<double>(out var doubleValue) => doubleValue,
            JsonValue value when value.TryGetValue<string>(out var stringValue) => stringValue,
            JsonValue value when value.TryGetValue<JsonElement>(out var element) => ToClrValue(element),
            _ => node.ToJsonString()
        };
    }

    private static object? ToClrValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(static property => property.Name, static property => ToClrValue(property.Value), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(ToClrValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };

    private static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            JsonNode node => node.DeepClone(),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            _ => JsonSerializer.SerializeToNode(value)
        };
    }

    private static string? GetStringProperty(object instance, string propertyName)
        => GetPropertyValue(instance, propertyName)?.ToString();

    private static bool? GetBoolProperty(object instance, string propertyName)
    {
        var value = GetPropertyValue(instance, propertyName);
        return value switch
        {
            bool boolValue => boolValue,
            null => null,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static object? GetPropertyValue(object? instance, params string[] propertyPath)
    {
        var current = instance;
        foreach (var propertyName in propertyPath)
        {
            if (current is null)
            {
                return null;
            }

            var property = current.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                return null;
            }

            current = property.GetValue(current);
        }

        return current;
    }

    private static IEnumerable<object> EnumerateObjects(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string)
        {
            yield return value;
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            yield break;
        }

        yield return value;
    }

    private static byte[]? TryGetBytes(object? value)
        => value switch
        {
            null => null,
            byte[] bytes => bytes,
            Memory<byte> memory => memory.ToArray(),
            ReadOnlyMemory<byte> readOnlyMemory => readOnlyMemory.ToArray(),
            ArraySegment<byte> segment => segment.ToArray(),
            IEnumerable<byte> enumerable => enumerable.ToArray(),
            _ => TryInvokeToArray(value)
        };

    private static byte[]? TryInvokeToArray(object value)
    {
        var method = value.GetType().GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance, []);
        return method?.Invoke(value, null) as byte[];
    }

    private static void SetProperty(object target, object? value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null || !property.CanWrite)
            {
                continue;
            }

            property.SetValue(target, ConvertValue(value, property.PropertyType));
            return;
        }
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
        {
            return null;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType.IsEnum && value is string enumText)
        {
            return Enum.Parse(targetType, enumText, true);
        }

        if (targetType == typeof(Uri) && value is string uriText)
        {
            return new Uri(uriText, UriKind.Absolute);
        }

        if (typeof(IDictionary<string, string>).IsAssignableFrom(targetType) && value is IEnumerable<KeyValuePair<string, string>> kvps)
        {
            return kvps.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        if (typeof(IEnumerable<string>).IsAssignableFrom(targetType) && value is IEnumerable<string> strings)
        {
            return strings.ToList();
        }

        return Convert.ChangeType(value, targetType);
    }

    private sealed record OperationResult<T>(T? Value, string? Error);
}
