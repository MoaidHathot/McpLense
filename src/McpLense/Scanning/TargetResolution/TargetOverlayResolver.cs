namespace McpLense.Scanning.TargetResolution;

/// <summary>
/// Computes the merged <see cref="TargetOverlay"/> for a given URL given the user's config
/// blocks and CLI-supplied headers. The merge order is:
/// <list type="number">
///   <item>Every matching <see cref="TargetPatternEntry"/> (in declaration order, least
///   specific to most specific within the patterns list).</item>
///   <item>The matching <see cref="ScanTargetEntry"/> when one is found (exact URL match,
///   normalised by trimming a trailing slash on the path).</item>
///   <item>CLI flags (passed in as <paramref name="cliHeaders"/> etc.).</item>
/// </list>
/// Per-header-key last-write-wins; per-other-field "later non-null wins".
///
/// The resolver is pure - no I/O, no config-file loading. The config-loading layer is
/// responsible for materialising the <see cref="ScanConfig"/> before calling
/// <see cref="Resolve"/>.
/// </summary>
internal static class TargetOverlayResolver
{
    /// <summary>
    /// Looks up the target entry for <paramref name="reference"/> on the config. The
    /// reference may be either an exact URL or a <c>@name</c> lookup (case-insensitive). When
    /// the reference is a URL, the resolver also tries to auto-resolve to a matching
    /// <see cref="ScanTargetEntry.Url"/>.
    /// </summary>
    public static ScanTargetEntry? FindTargetEntry(
        ScanConfig config,
        Uri url,
        string? namedReference = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(url);

        if (!string.IsNullOrEmpty(namedReference))
        {
            foreach (var entry in config.Targets)
            {
                if (!string.IsNullOrEmpty(entry.Name)
                    && string.Equals(entry.Name, namedReference, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            // Named reference supplied but no entry has that name. Auto-resolution by URL
            // should still apply when the URL parameter matches anything declared.
        }

        foreach (var entry in config.Targets)
        {
            if (!string.IsNullOrEmpty(entry.Url) && UrlsMatch(entry.Url!, url))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the URL of a named target entry (e.g. when the CLI passes <c>@foo</c> and the
    /// resolver needs to know the actual URL before the scan begins).
    /// </summary>
    public static string? ResolveNamedTargetUrl(ScanConfig config, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(name);

        foreach (var entry in config.Targets)
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Url;
            }
        }

        return null;
    }

    /// <summary>
    /// Computes the final overlay for one MCP URL. CLI parameters always win.
    /// </summary>
    /// <param name="config">Parsed scan configuration (the source of patterns + named targets).</param>
    /// <param name="url">Resolved MCP URL.</param>
    /// <param name="namedReference">Optional <c>@name</c> reference (without the '@') passed by the user.</param>
    /// <param name="cliHeaders">CLI <c>--header</c> overrides.</param>
    /// <param name="cliProfile">CLI <c>--profile</c> override.</param>
    /// <param name="cliTransport">CLI <c>--transport</c> override; null when "auto".</param>
    /// <param name="cliTimeout">CLI <c>--timeout</c> value; null when the default was kept.</param>
    /// <param name="cliDisables">CLI <c>--disable</c> set.</param>
    public static TargetOverlay Resolve(
        ScanConfig config,
        Uri url,
        string? namedReference,
        IReadOnlyDictionary<string, string>? cliHeaders,
        string? cliProfile,
        TransportPreference? cliTransport,
        TimeSpan? cliTimeout,
        IReadOnlySet<string>? cliDisables)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(url);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var disabledChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedPatterns = new List<string>();
        TargetScope scope = TargetScope.All;
        string? profile = null;
        TransportPreference? transport = null;
        TimeSpan? timeout = null;

        // 1. Patterns (least specific). Apply in declaration order so later patterns override
        //    earlier ones on per-key conflicts.
        foreach (var pattern in config.TargetPatterns)
        {
            if (string.IsNullOrEmpty(pattern.Match))
            {
                continue;
            }

            if (!UrlGlob.TryCompile(pattern.Match!, out var glob, out _) || glob is null)
            {
                // Compile errors were already surfaced at load time via stderr warning.
                continue;
            }

            if (!glob.IsMatch(url))
            {
                continue;
            }

            matchedPatterns.Add(pattern.Match!);

            if (pattern.Headers is not null)
            {
                foreach (var (k, v) in pattern.Headers)
                {
                    headers[k] = v;
                }
            }

            if (pattern.Scope.HasValue)
            {
                scope = pattern.Scope.Value;
            }

            if (!string.IsNullOrEmpty(pattern.Profile))
            {
                profile = pattern.Profile;
            }

            if (!string.IsNullOrEmpty(pattern.Transport))
            {
                transport = ParseTransport(pattern.Transport!);
            }

            if (pattern.TimeoutSeconds.HasValue)
            {
                timeout = TimeSpan.FromSeconds(pattern.TimeoutSeconds.Value);
            }

            if (pattern.DisabledChecks is not null)
            {
                foreach (var id in pattern.DisabledChecks)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        disabledChecks.Add(id);
                    }
                }
            }
        }

        // 2. Named target entry (more specific).
        var targetEntry = FindTargetEntry(config, url, namedReference);
        if (targetEntry is not null)
        {
            if (targetEntry.Headers is not null)
            {
                foreach (var (k, v) in targetEntry.Headers)
                {
                    headers[k] = v;
                }
            }

            if (targetEntry.Scope.HasValue)
            {
                scope = targetEntry.Scope.Value;
            }

            if (!string.IsNullOrEmpty(targetEntry.Profile))
            {
                profile = targetEntry.Profile;
            }

            if (!string.IsNullOrEmpty(targetEntry.Transport))
            {
                transport = ParseTransport(targetEntry.Transport!);
            }

            if (targetEntry.TimeoutSeconds.HasValue)
            {
                timeout = TimeSpan.FromSeconds(targetEntry.TimeoutSeconds.Value);
            }

            if (targetEntry.DisabledChecks is not null)
            {
                foreach (var id in targetEntry.DisabledChecks)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        disabledChecks.Add(id);
                    }
                }
            }
        }

        // 3. CLI flags (most specific).
        if (cliHeaders is not null)
        {
            foreach (var (k, v) in cliHeaders)
            {
                headers[k] = v;
            }
        }

        if (!string.IsNullOrEmpty(cliProfile))
        {
            profile = cliProfile;
        }

        if (cliTransport.HasValue && cliTransport.Value != TransportPreference.Auto)
        {
            transport = cliTransport.Value;
        }

        if (cliTimeout.HasValue)
        {
            timeout = cliTimeout.Value;
        }

        if (cliDisables is not null)
        {
            foreach (var id in cliDisables)
            {
                disabledChecks.Add(id);
            }
        }

        return new TargetOverlay(
            Headers: headers,
            Scope: scope,
            Profile: profile,
            Transport: transport,
            Timeout: timeout,
            DisabledChecks: disabledChecks.Count == 0 ? Array.Empty<string>() : disabledChecks.ToArray(),
            MatchedPatterns: matchedPatterns.Count == 0 ? Array.Empty<string>() : matchedPatterns.ToArray(),
            MatchedTargetName: targetEntry?.Name);
    }

    /// <summary>
    /// Compares an entry's declared URL with a runtime URL. Matching is case-insensitive on
    /// scheme + host, case-sensitive on path, and trailing slashes on the path are ignored
    /// for the comparison so <c>https://x/y</c> and <c>https://x/y/</c> resolve to the same
    /// target.
    /// </summary>
    private static bool UrlsMatch(string declared, Uri url)
    {
        if (!Uri.TryCreate(declared, UriKind.Absolute, out var declaredUri))
        {
            return false;
        }

        if (!string.Equals(declaredUri.Scheme, url.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(declaredUri.Host, url.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (declaredUri.Port != url.Port)
        {
            return false;
        }

        return string.Equals(
            NormalizePath(declaredUri.AbsolutePath),
            NormalizePath(url.AbsolutePath),
            StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        if (path.Length > 1 && path.EndsWith('/'))
        {
            return path.TrimEnd('/');
        }

        return path;
    }

    private static TransportPreference ParseTransport(string raw)
        => raw.ToLowerInvariant() switch
        {
            "auto" => TransportPreference.Auto,
            "streamable-http" or "streamablehttp" or "http" => TransportPreference.StreamableHttp,
            "sse" => TransportPreference.Sse,
            _ => throw new UserInputException($"Unknown transport '{raw}' in target configuration.")
        };
}
