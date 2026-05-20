using McpLense.Diagnostics;
using McpLense.Scanning.TargetResolution;
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
        Action<int, int, string, TimeSpan>? progress = null,
        bool quiet = false,
        bool verbose = false,
        IReadOnlyList<string>? scanPluginPaths = null)
    {
        // Load merged config + profiles from the same paths the user gave (or XDG defaults).
        // We do this BEFORE TargetResolver so a positional @name reference can be resolved
        // against the config's `targets[]` block.
        var resolvedPaths = TargetConfigLoading.ResolveScanConfigPaths(target.ProfilePaths);
        var profiles = resolvedPaths.Count == 0
            ? Array.Empty<AuthProfile>()
            : await ProfileLoader.LoadAsync(resolvedPaths, new EnvironmentExpander(), cancellationToken).ConfigureAwait(false);
        var scanConfig = await ScanConfigLoader.LoadAsync(resolvedPaths, cancellationToken).ConfigureAwait(false);

        // @name resolution: turn the named reference into a URL by looking it up against
        // the config file's `targets[]` entries. Fail fast when the name doesn't match.
        target = TargetOverlayApplicator.ResolveNamedReference(target, scanConfig);

        var servers = await TargetResolver.ResolveAsync(target, cancellationToken).ConfigureAwait(false);

        // Apply per-server overlay (headers, scope, transport, timeout, disabledChecks).
        // Same helper used by McpExecutor so non-scan commands behave identically.
        var overlaidServers = TargetOverlayApplicator.Apply(
            servers,
            scanConfig,
            target,
            cliDisables,
            quiet,
            verbose);

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

        // Plugin checks load AFTER the built-ins so a plugin can shadow a built-in by
        // declaring the same Id (ScanPipelineBuilder.AddCheck replaces by id). Load failures
        // surface as ScanPluginException; the dispatcher catches and rethrows with the file
        // path for actionability.
        if (scanPluginPaths is { Count: > 0 })
        {
            foreach (var pluginPath in scanPluginPaths)
            {
                IReadOnlyList<IScanCheck> pluginChecks;
                try
                {
                    pluginChecks = Directory.Exists(pluginPath)
                        ? ScanPluginLoader.LoadFromDirectory(pluginPath)
                        : ScanPluginLoader.LoadFromAssemblyPath(pluginPath);
                }
                catch (ScanPluginException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ScanPluginException($"Failed to load scan plugin '{pluginPath}': {ex.Message}", ex);
                }

                foreach (var check in pluginChecks)
                {
                    pipeline.AddCheck(check);
                    if (!quiet)
                    {
                        McpLenseLog.Write($"scan-plugin: loaded {check.GetType().FullName} (id={check.Id}) from {pluginPath}");
                    }
                }
            }
        }

        foreach (var id in cliEnables ?? (IReadOnlySet<string>)new HashSet<string>())
        {
            pipeline.Enable(id);
        }

        foreach (var id in cliDisables ?? (IReadOnlySet<string>)new HashSet<string>())
        {
            pipeline.Disable(id);
        }

        return await pipeline.Build().RunAsync(overlaidServers, handshakeTimeout, cancellationToken, maxDegreeOfParallelism, progress).ConfigureAwait(false);
    }
}
