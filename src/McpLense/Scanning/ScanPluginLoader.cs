using System.Reflection;
using System.Runtime.Loader;

namespace McpLense.Scanning;

/// <summary>
/// Loads third-party <see cref="IScanCheck"/> implementations from external assemblies into
/// an isolated <see cref="AssemblyLoadContext"/> that shares only the host's
/// <see cref="McpLense"/> assembly. Plugins compile against <c>McpLense</c> as a normal
/// <c>PackageReference</c>; at runtime the loader resolves <c>McpLense</c> from the host
/// (so types like <see cref="IScanCheck"/> are identity-equal across the boundary) while
/// keeping every other plugin dependency private to the plugin's load context.
/// </summary>
/// <remarks>
/// <para>
/// Discovery: every <see cref="IScanCheck"/>-implementing public type with a public
/// parameterless constructor is loaded. Abstract types and types lacking the constructor
/// are skipped silently. Constructor failures are surfaced as <see cref="ScanPluginException"/>.
/// </para>
/// <para>
/// Isolation: the host's <c>McpLense</c> assembly is shared (so check identity holds) but
/// every other dependency is loaded into the plugin's <see cref="AssemblyLoadContext"/>
/// from the plugin directory. This means plugin authors can ship their own copies of
/// JSON serializers, HTTP libraries, etc. without colliding with the host's.
/// </para>
/// <para>
/// Unloading is supported (the <see cref="AssemblyLoadContext"/> is collectible) but the
/// CLI lives for one process and never unloads. The <see cref="ScanPluginException"/>
/// captures load failures so the CLI can report them without aborting other plugins.
/// </para>
/// </remarks>
public static class ScanPluginLoader
{
    /// <summary>
    /// Loads every <see cref="IScanCheck"/> exported by the given assembly file. Throws
    /// <see cref="ScanPluginException"/> on file-not-found or load failure; returns an
    /// empty list when the assembly loads but exports no compatible types.
    /// </summary>
    /// <param name="assemblyPath">Absolute or working-directory-relative path to a .NET assembly (.dll).</param>
    /// <returns>The instantiated checks ready for <see cref="ScanPipelineBuilder.AddCheck(IScanCheck)"/>.</returns>
    public static IReadOnlyList<IScanCheck> LoadFromAssemblyPath(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);

        var full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
        {
            throw new ScanPluginException($"Plugin assembly not found: {full}");
        }

        var alc = new ScanPluginLoadContext(full);
        Assembly assembly;
        try
        {
            assembly = alc.LoadFromAssemblyPath(full);
        }
        catch (Exception ex)
        {
            throw new ScanPluginException($"Failed to load plugin assembly '{full}': {ex.Message}", ex);
        }

        var checkType = typeof(IScanCheck);
        var found = new List<IScanCheck>();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Surface the per-type loader errors so the user gets actionable info; the
            // loader-exception array often points at exactly which type's dependency is missing.
            var messages = (ex.LoaderExceptions ?? Array.Empty<Exception?>())
                .Where(e => e is not null)
                .Select(e => e!.Message);
            throw new ScanPluginException(
                $"Plugin '{full}' failed to enumerate types: {ex.Message} ({string.Join("; ", messages)})",
                ex);
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !type.IsClass || !checkType.IsAssignableFrom(type))
            {
                continue;
            }

            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor is null)
            {
                // No parameterless ctor: plugin author can register via a future
                // ServiceProvider-aware overload. For now we skip silently rather than
                // failing the whole plugin - this lets a single assembly mix activatable
                // and DI-only checks.
                continue;
            }

            try
            {
                var instance = (IScanCheck)ctor.Invoke(null);
                found.Add(instance);
            }
            catch (Exception ex)
            {
                throw new ScanPluginException(
                    $"Plugin check '{type.FullName}' threw from its parameterless constructor: {ex.Message}",
                    ex);
            }
        }

        return found;
    }

    /// <summary>
    /// Convenience: load every <c>*.dll</c> in the given directory. Order is alphabetical so
    /// plugin authors can rely on file-name prefixes for ordering when it matters.
    /// </summary>
    public static IReadOnlyList<IScanCheck> LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
        {
            throw new ScanPluginException($"Plugin directory not found: {directory}");
        }

        var all = new List<IScanCheck>();
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            all.AddRange(LoadFromAssemblyPath(dll));
        }

        return all;
    }

    /// <summary>
    /// Per-plugin <see cref="AssemblyLoadContext"/>. Resolves the host's loaded
    /// <c>McpLense</c> assembly first (so <see cref="IScanCheck"/> identity is shared), then
    /// falls back to <see cref="AssemblyDependencyResolver"/> probing the plugin directory.
    /// </summary>
    private sealed class ScanPluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public ScanPluginLoadContext(string pluginPath)
            : base(name: $"mcplense-plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Critical: when the plugin references the host's McpLense assembly (it has to,
            // because IScanCheck lives there), return the SAME loaded instance the host is
            // using. Otherwise the plugin's IScanCheck would be a distinct type and the
            // `is IScanCheck` check would fail at runtime.
            if (string.Equals(assemblyName.Name, "McpLense", StringComparison.Ordinal))
            {
                return null; // Defer to Default ALC, which already has the host's McpLense.
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

/// <summary>
/// Raised when a plugin assembly cannot be located, loaded, or instantiated. Carries the
/// inner exception (usually a load / reflection error) so the CLI can render an actionable
/// stack trace without losing the underlying cause.
/// </summary>
public sealed class ScanPluginException : Exception
{
    public ScanPluginException(string message) : base(message) { }
    public ScanPluginException(string message, Exception innerException) : base(message, innerException) { }
}
