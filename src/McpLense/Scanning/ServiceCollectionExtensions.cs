using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace McpLense.Scanning;

/// <summary>
/// DI integration entry points. Hosts that already have a <see cref="IServiceCollection"/>
/// (web apps, generic-host workers, integration test fixtures) can use these to plug
/// McpLense's scanning pipeline into the existing service graph.
/// </summary>
public static class McpLenseServiceCollectionExtensions
{
    /// <summary>Stable name for the HttpClient probes should resolve via <c>IHttpClientFactory</c>.</summary>
    public const string ProbeHttpClientName = "mcplense-probe";

    /// <summary>
    /// Registers the default <see cref="ScanPipeline"/>, default <see cref="ScanConfig"/>,
    /// and every built-in <see cref="IScanCheck"/>. Call this first, then chain
    /// <see cref="AddScanCheck{T}"/> for any custom checks.
    /// </summary>
    public static IServiceCollection AddMcpLense(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ScanConfig>(_ => new ScanConfig());

        // Single shared HttpClient pool for every check that talks to outbound HTTP. Reuses
        // sockets across the fleet scan; per-check timeouts go on the request, not the
        // factory-level client. Probes that need cert capture (TransportProbe) still own
        // their own HttpClient because cert capture requires a custom handler.
        services.AddHttpClient(ProbeHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        foreach (var check in BuiltInChecks.Create())
        {
            services.AddSingleton<IScanCheck>(check);
        }

        services.TryAddSingleton<ScanPipeline>(sp =>
        {
            var checks = sp.GetServices<IScanCheck>().ToArray();
            var config = sp.GetRequiredService<ScanConfig>();
            return new ScanPipeline(checks, config, sp);
        });

        return services;
    }

    /// <summary>
    /// Registers a custom check; type-parameter overload. Same id collisions with built-ins
    /// transparently replace the built-in (last registration wins under default DI rules).
    /// </summary>
    public static IServiceCollection AddScanCheck<T>(this IServiceCollection services) where T : class, IScanCheck
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IScanCheck, T>();
        return services;
    }

    /// <summary>
    /// Loads a config file and registers the resulting <see cref="ScanConfig"/> as a
    /// singleton. Replaces any previously registered <see cref="ScanConfig"/>.
    /// </summary>
    public static IServiceCollection AddMcpLenseConfigFromFile(this IServiceCollection services, string path)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(path);

        services.AddSingleton<ScanConfig>(sp =>
            ScanConfigLoader.LoadAsync(new[] { path }, CancellationToken.None).GetAwaiter().GetResult());

        return services;
    }
}
