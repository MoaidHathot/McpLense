using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpLense.Scanning;

/// <summary>
/// Fluent builder for ad-hoc <see cref="ScanPipeline"/> construction. Most consumers will
/// reach for <c>IServiceCollection.AddMcpLense()</c> instead; this builder is the
/// no-DI escape hatch (one-shot scripts, tests, simple integrations).
/// </summary>
public sealed class ScanPipelineBuilder
{
    private readonly List<IScanCheck> _checks = new();
    private ScanConfig _config = new();
    private IServiceProvider? _services;
    private ILoggerFactory? _loggerFactory;
    private readonly HashSet<string> _enables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds every built-in check that ships with the library at default settings.</summary>
    public ScanPipelineBuilder AddDefaultChecks()
    {
        foreach (var check in BuiltInChecks.Create())
        {
            _checks.Add(check);
        }

        return this;
    }

    /// <summary>Adds an additional check (or replaces a built-in with a custom one with the same id).</summary>
    public ScanPipelineBuilder AddCheck(IScanCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var existing = _checks.FindIndex(c => string.Equals(c.Id, check.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _checks[existing] = check;
        }
        else
        {
            _checks.Add(check);
        }

        return this;
    }

    /// <summary>Type-parameter convenience that resolves the check via reflection's parameterless ctor.</summary>
    public ScanPipelineBuilder AddCheck<T>() where T : IScanCheck, new()
        => AddCheck(new T());

    /// <summary>Replaces the current <see cref="ScanConfig"/> wholesale.</summary>
    public ScanPipelineBuilder UseConfig(ScanConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        return this;
    }

    /// <summary>Loads a config file from disk and uses it.</summary>
    public async Task<ScanPipelineBuilder> UseConfigFileAsync(string path, CancellationToken cancellationToken = default)
    {
        _config = await ScanConfigLoader.LoadAsync(new[] { path }, cancellationToken).ConfigureAwait(false);
        return this;
    }

    /// <summary>Supplies an explicit DI provider; otherwise a minimal one is built.</summary>
    public ScanPipelineBuilder UseServices(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        return this;
    }

    /// <summary>Supplies a logger factory; checks resolve <c>ILogger&lt;T&gt;</c> from it.</summary>
    public ScanPipelineBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>Force-enables a check id, overriding both default and config.</summary>
    public ScanPipelineBuilder Enable(string checkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkId);
        _enables.Add(checkId);
        return this;
    }

    /// <summary>Force-disables a check id, overriding both default and config.</summary>
    public ScanPipelineBuilder Disable(string checkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkId);
        _disables.Add(checkId);
        return this;
    }

    /// <summary>Finalises the builder into a runnable pipeline.</summary>
    public ScanPipeline Build()
    {
        var services = _services ?? BuildMinimalServiceProvider();
        var logger = (_loggerFactory ?? services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger<ScanPipeline>();
        return new ScanPipeline(_checks, _config, services, logger, _enables, _disables);
    }

    private IServiceProvider BuildMinimalServiceProvider()
    {
        var sc = new ServiceCollection();
        if (_loggerFactory is not null)
        {
            sc.AddSingleton(_loggerFactory);
        }
        return sc.BuildServiceProvider();
    }
}
