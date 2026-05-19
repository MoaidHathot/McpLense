using System.Text;
using System.Text.RegularExpressions;

namespace McpLense.Scanning.TargetResolution;

/// <summary>
/// Compiles a URL-level glob expression into a predicate. The grammar matches the user-
/// facing documentation for the <c>targetPatterns[].match</c> field:
/// <list type="bullet">
///   <item><c>*</c> matches any sequence of characters EXCEPT <c>/</c> (single host label or
///   single path segment).</item>
///   <item><c>**</c> matches any sequence INCLUDING <c>/</c> (any number of path segments).</item>
///   <item><c>?</c> matches exactly one non-<c>/</c> character.</item>
///   <item>Every other character is literal.</item>
/// </list>
///
/// Matching semantics:
/// <list type="bullet">
///   <item>The scheme (e.g. <c>https</c>) and host are case-INSENSITIVE.</item>
///   <item>The path is case-SENSITIVE (matches browser convention).</item>
///   <item>The query string and fragment are STRIPPED from the candidate URL before matching
///   - patterns don't need to think about query/fragment ordering, and headers can't depend on
///   query strings anyway.</item>
///   <item>The pattern is anchored at both ends (implicit <c>^...$</c>); a pattern matches the
///   FULL URL.</item>
///   <item>Patterns without an explicit scheme (e.g. <c>example.com/foo</c>) are rejected -
///   match the URL we'd actually see ("https://example.com/foo") so behaviour is unambiguous.</item>
/// </list>
/// </summary>
internal sealed class UrlGlob
{
    private readonly Regex _hostRegex;
    private readonly Regex _pathRegex;
    private readonly string _rawPattern;

    private UrlGlob(string rawPattern, Regex hostRegex, Regex pathRegex)
    {
        _rawPattern = rawPattern;
        _hostRegex = hostRegex;
        _pathRegex = pathRegex;
    }

    public string Pattern => _rawPattern;

    /// <summary>
    /// Compiles a pattern. Throws <see cref="UserInputException"/> when the pattern is
    /// missing a scheme or otherwise malformed.
    /// </summary>
    public static UrlGlob Compile(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        // We split the pattern at the FIRST '/' that follows the scheme separator. The scheme
        // separator is "://" verbatim - patterns must include it so we can mechanically split
        // host from path without re-implementing URL parsing.
        var schemeSep = pattern.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep < 0)
        {
            throw new UserInputException(
                $"URL pattern '{pattern}' is missing a scheme. Use a fully-qualified pattern, " +
                "e.g. 'https://*.example.com/**'.");
        }

        // Scheme is everything before "://". We allow wildcards in the scheme so '*' covers
        // http+https together, but we DON'T allow '/' inside the scheme.
        var scheme = pattern[..schemeSep];
        if (scheme.Contains('/', StringComparison.Ordinal))
        {
            throw new UserInputException($"URL pattern '{pattern}' has '/' in the scheme.");
        }

        // After "://" comes host[:port][/path]. The first '/' after schemeSep+3 separates
        // host from path.
        var hostStart = schemeSep + 3;
        var pathStart = pattern.IndexOf('/', hostStart);
        string hostPart;
        string pathPart;
        if (pathStart < 0)
        {
            hostPart = pattern[hostStart..];
            pathPart = string.Empty;
        }
        else
        {
            hostPart = pattern.Substring(hostStart, pathStart - hostStart);
            pathPart = pattern[pathStart..];
        }

        if (hostPart.Length == 0)
        {
            throw new UserInputException($"URL pattern '{pattern}' has an empty host.");
        }

        // Normalise default ports out of the pattern's host part. URIs in .NET canonicalise
        // default ports out of `Uri.Authority`, so the runtime URL we compare against will
        // NOT carry `:443` for https or `:80` for http. Strip those from the pattern too so
        // a literal `:443` in the pattern still matches when the URL omits it.
        hostPart = NormaliseDefaultPort(scheme, hostPart);

        // Build two regexes:
        //   - hostRegex matches scheme + "://" + host (case-insensitive).
        //   - pathRegex matches the path (case-sensitive).
        // We compile them separately because their case-sensitivity differs.
        var schemeAndHost = scheme + "://" + hostPart;
        var hostPattern = "^" + GlobToRegex(schemeAndHost, hostMode: true) + "$";
        var pathPattern = "^" + GlobToRegex(pathPart, hostMode: false) + "$";

        var hostRegex = new Regex(hostPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        var pathRegex = new Regex(pathPattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

        return new UrlGlob(pattern, hostRegex, pathRegex);
    }

    /// <summary>
    /// Tries to compile the pattern. Returns false (and a diagnostic message) instead of
    /// throwing - used by config loaders that want to warn-and-skip a bad entry.
    /// </summary>
    public static bool TryCompile(string pattern, out UrlGlob? glob, out string? error)
    {
        try
        {
            glob = Compile(pattern);
            error = null;
            return true;
        }
        catch (UserInputException ex)
        {
            glob = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Returns true when this pattern matches <paramref name="url"/>.
    /// </summary>
    public bool IsMatch(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var scheme = url.Scheme;
        var hostPort = url.IsDefaultPort
            ? url.Host
            : $"{url.Host}:{url.Port}";
        var schemeAndHost = $"{scheme}://{hostPort}";
        if (!_hostRegex.IsMatch(schemeAndHost))
        {
            return false;
        }

        var path = url.AbsolutePath;
        return _pathRegex.IsMatch(path);
    }

    private static string NormaliseDefaultPort(string scheme, string hostPart)
    {
        // Default-port stripping only fires when the scheme is a literal http/https. Wildcard
        // schemes ('*') keep whatever the user wrote.
        var colon = hostPart.LastIndexOf(':');
        if (colon < 0)
        {
            return hostPart;
        }

        var portText = hostPart[(colon + 1)..];
        if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) && portText == "443")
        {
            return hostPart[..colon];
        }

        if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) && portText == "80")
        {
            return hostPart[..colon];
        }

        return hostPart;
    }

    private static string GlobToRegex(string glob, bool hostMode)
    {
        // In hostMode, '*' is a single LABEL (matches no '/' and no '.'). '**' matches any
        // sequence including '/' and '.'. In path mode, '*' matches a single SEGMENT (no
        // '/'); '**' matches any sequence including '/'.
        var singleStar = hostMode ? "[^/.]*" : "[^/]*";
        var sb = new StringBuilder(glob.Length * 2);
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // '**' - any sequence including '/' (and '.' in host mode).
                        sb.Append(".*");
                        i++;
                    }
                    else
                    {
                        sb.Append(singleStar);
                    }
                    break;
                case '?':
                    sb.Append(hostMode ? "[^/.]" : "[^/]");
                    break;
                default:
                    // Escape every regex metachar to keep the pattern literal.
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return sb.ToString();
    }
}
