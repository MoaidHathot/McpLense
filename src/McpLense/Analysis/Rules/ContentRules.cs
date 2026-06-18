using System.Text.RegularExpressions;
using McpLense.Scanning;

namespace McpLense.Analysis.Rules;

/// <summary>
/// Looks for prompt-injection signals in the verbatim text an LLM host will read - tool/prompt
/// descriptions, server instructions, server info. Two classes of signal: hidden characters (bidi
/// overrides, zero-width, control chars) that conceal instructions from a human reviewer, and overt
/// instruction-hijacking phrases. This is the highest-value MCP-specific check: a poisoned tool
/// description is invisible in most UIs but fully visible to the model.
/// </summary>
public sealed partial class PromptInjectionRule : IFindingRule
{
    public string Id => "prompt-injection";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        foreach (var (path, text) in TextFields(facts))
        {
            if (FindHiddenChars(text) is { } hidden)
            {
                yield return new Finding(
                    Id,
                    Severity.High,
                    $"Hidden/control characters in model-visible text ({hidden})",
                    path,
                    hidden,
                    "Remove bidi/zero-width/control characters from this text - they hide instructions from human reviewers while remaining visible to the model.");
            }

            if (FindSuspiciousPhrase(text) is { } phrase)
            {
                yield return new Finding(
                    Id,
                    Severity.Medium,
                    $"Instruction-hijacking phrase in model-visible text (\"{phrase}\")",
                    path,
                    phrase,
                    "Review this text for prompt injection - phrases that try to override the host's instructions do not belong in a tool/prompt description.");
            }
        }
    }

    private static IEnumerable<(string Path, string Text)> TextFields(ServerScanResult facts)
    {
        foreach (var tool in facts.ToolItems())
        {
            if (tool.Str("description") is { Length: > 0 } d)
            {
                yield return ($"checks.tools.items[name={tool.Str("name") ?? "?"}].description", d);
            }
        }

        foreach (var prompt in facts.PromptItems())
        {
            if (prompt.Str("description") is { Length: > 0 } d)
            {
                yield return ($"checks.prompts.items[name={prompt.Str("name") ?? "?"}].description", d);
            }
        }

        if (facts.Check("serverInfo").Str("description") is { Length: > 0 } info)
        {
            yield return ("checks.serverInfo.description", info);
        }

        if (facts.Check("protocol").Str("instructions") is { Length: > 0 } instructions)
        {
            yield return ("checks.protocol.instructions", instructions);
        }
    }

    /// <summary>Returns a label for the first hidden/control char found, or null when the text is clean.</summary>
    internal static string? FindHiddenChars(string text)
    {
        foreach (var ch in text)
        {
            var label = ch switch
            {
                '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' => $"U+{(int)ch:X4} bidi override/embedding",
                '\u2066' or '\u2067' or '\u2068' or '\u2069' => $"U+{(int)ch:X4} bidi isolate",
                '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' => $"U+{(int)ch:X4} zero-width",
                _ when (ch < '\u0020' && ch is not ('\t' or '\n' or '\r')) || (ch >= '\u0080' && ch <= '\u009F') => $"U+{(int)ch:X4} control",
                _ => null
            };

            if (label is not null)
            {
                return label;
            }
        }

        return null;
    }

    /// <summary>Returns the first instruction-hijacking phrase matched, or null.</summary>
    internal static string? FindSuspiciousPhrase(string text)
    {
        var match = SuspiciousPhrase().Match(text);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(
        @"ignore (all )?(previous|prior|above)|disregard (all )?(previous|prior)|forget (all )?(previous|your)|you are now|new instructions:|system prompt|</?system>|do not tell|without (telling|informing) the user",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuspiciousPhrase();
}

/// <summary>
/// URLs embedded in tool/prompt descriptions are rendered by some hosts - a vector for data
/// exfiltration (e.g. an image URL the host fetches, carrying data in the query string).
/// </summary>
public sealed class DescriptionUrlRule : IFindingRule
{
    public string Id => "description-url";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        var fields = facts.Check("metrics").Array("fields");
        if (fields is null)
        {
            yield break;
        }

        foreach (var field in fields)
        {
            var path = field.Str("path");
            if (path is null || (!path.StartsWith("tool:", StringComparison.Ordinal) && !path.StartsWith("prompt:", StringComparison.Ordinal)))
            {
                continue;
            }

            if ((field.Int("urlCount") ?? 0) <= 0)
            {
                continue;
            }

            var urls = (field.Array("urls"))?.Select(u => u.AsStr()).Where(u => u is not null) ?? [];
            yield return new Finding(
                Id,
                Severity.Low,
                $"URL(s) embedded in description ({path})",
                $"checks.metrics.fields[path={path}].urls",
                string.Join(", ", urls),
                "Confirm these URLs are expected - hosts that render description links/images can be used to exfiltrate data.");
        }
    }
}

/// <summary>
/// The error response to a non-existent tool call sometimes leaks internals (stack traces, file
/// paths, build identifiers, internal hostnames) that aid an attacker.
/// </summary>
public sealed partial class ErrorLeakRule : IFindingRule
{
    public string Id => "error-info-leak";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        var b = facts.Check("behavior.callNonExistentTool");
        if (b is null)
        {
            yield break;
        }

        foreach (var (prop, label) in new[] { ("toolResultJson", "tool result"), ("jsonRpcErrorMessage", "JSON-RPC error message"), ("jsonRpcErrorData", "JSON-RPC error data") })
        {
            var value = b[prop]?.ToString();
            if (string.IsNullOrEmpty(value) || InternalMarker().Match(value) is not { Success: true } match)
            {
                continue;
            }

            yield return new Finding(
                Id,
                Severity.Medium,
                $"Error response to an unknown tool leaks internals ({label})",
                $"checks.behavior.callNonExistentTool.{prop}",
                Excerpt(value, match.Index),
                "Return a generic error for unknown tools - do not include stack traces, file paths, build ids, or internal hostnames.");
        }
    }

    private static string Excerpt(string value, int at)
    {
        var start = Math.Max(0, at - 20);
        var len = Math.Min(120, value.Length - start);
        return (start > 0 ? "..." : string.Empty) + value.Substring(start, len).Replace('\n', ' ').Replace('\r', ' ') + "...";
    }

    [GeneratedRegex(
        @"(at [A-Za-z]:\\|Traceback \(most recent call|\bat [\w.]+\([^)]*:\d+\)|build=|internal-|\.cs:line \d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex InternalMarker();
}

/// <summary>
/// The server returned a 5xx to deliberately malformed JSON-RPC (from the opt-in
/// <c>behavior.callMalformed</c> check) - it does not reject bad input gracefully, which can mean a
/// crash, a DoS lever, or internal leakage. Yields nothing unless that check ran.
/// </summary>
public sealed class MalformedHandlingRule : IFindingRule
{
    public string Id => "malformed-handling";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        var probes = facts.Check("behavior.callMalformed").Array("probes");
        if (probes is null)
        {
            yield break;
        }

        foreach (var probe in probes)
        {
            var status = probe.Int("statusCode");
            if (status >= 500)
            {
                yield return new Finding(
                    Id,
                    Severity.Medium,
                    $"Server returned {status} to malformed input ({probe.Str("case")})",
                    $"checks.behavior.callMalformed.probes[case={probe.Str("case")}].statusCode",
                    status.ToString(),
                    "Reject malformed JSON-RPC with a 400 / JSON-RPC parse error, not a 5xx - a 5xx suggests the bad input reached and broke server logic.");
            }
        }
    }
}

