using Microsoft.Extensions.DependencyInjection;

namespace McpLense.Scanning;

/// <summary>
/// High-level entry point used by the CLI's <c>scan</c> command. Wires the static
/// <see cref="TargetResolver"/> output to the <see cref="ScanPipeline"/>, loads profiles
/// from <see cref="TargetOptions.ProfilePaths"/> + XDG auto-discovery, and returns the
/// final <see cref="ScanReport"/>.
/// </summary>
internal static class ScanCommandDispatcher
{
    public static async Task<ScanReport> RunAsync(
        TargetOptions target,
        TimeSpan handshakeTimeout,
        IReadOnlySet<string>? cliEnables,
        IReadOnlySet<string>? cliDisables,
        CancellationToken cancellationToken,
        int maxDegreeOfParallelism = 1,
        Action<int, int, string, TimeSpan>? progress = null)
    {
        var servers = await TargetResolver.ResolveAsync(target, cancellationToken).ConfigureAwait(false);

        // Load merged config + profiles from the same paths the user gave (or XDG defaults).
        var resolvedPaths = ResolveProfilePaths(target.ProfilePaths);
        var profiles = resolvedPaths.Count == 0
            ? Array.Empty<AuthProfile>()
            : await ProfileLoader.LoadAsync(resolvedPaths, new EnvironmentExpander(), cancellationToken).ConfigureAwait(false);
        var scanConfig = await ScanConfigLoader.LoadAsync(resolvedPaths, cancellationToken).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyList<AuthProfile>>(profiles);
        services.AddSingleton(target.AuthOverrides);
        services.AddSingleton(scanConfig);
        // Pooled HttpClient (and DefaultRequestHeaders / SocketsHttpHandler reuse) for any
        // check that resolves IHttpClientFactory. Probes that need cert capture continue to
        // own their own clients.
        services.AddHttpClient(McpLenseServiceCollectionExtensions.ProbeHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        var provider = services.BuildServiceProvider();

        var pipeline = new ScanPipelineBuilder()
            .AddDefaultChecks()
            .UseConfig(scanConfig)
            .UseServices(provider);

        foreach (var id in cliEnables ?? (IReadOnlySet<string>)new HashSet<string>())
        {
            pipeline.Enable(id);
        }

        foreach (var id in cliDisables ?? (IReadOnlySet<string>)new HashSet<string>())
        {
            pipeline.Disable(id);
        }

        return await pipeline.Build().RunAsync(servers, handshakeTimeout, cancellationToken, maxDegreeOfParallelism, progress).ConfigureAwait(false);
    }

    /// <summary>
    /// Same profile-discovery semantics the existing executor uses: explicit
    /// <c>--profiles</c> / <c>--config</c> paths win; otherwise discover from the platform
    /// default config directory.
    /// </summary>
    private static IReadOnlyList<string> ResolveProfilePaths(IReadOnlyList<string> explicitPaths)
    {
        if (explicitPaths.Count > 0)
        {
            return explicitPaths;
        }

        var root = DefaultConfigPaths.ResolveRoot();
        var discovered = DefaultConfigPaths.EnumerateProfileFiles(root);

        // ALSO discover the unified config file - the user may have renamed
        // McpLense.Profiles.json -> McpLense.Config.json. We accept either name.
        if (root is not null)
        {
            var configFile = Path.Combine(root, ScanConfigLoader.ConfigFileName);
            if (File.Exists(configFile) && !discovered.Contains(configFile))
            {
                var merged = new List<string>(discovered) { configFile };
                return merged;
            }
        }

        return discovered;
    }
}
