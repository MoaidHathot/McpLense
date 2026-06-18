using McpLense.Analysis;
using McpLense.Scanning;

namespace McpLense.Learning;

/// <summary>Narrative explanation of one server: a list of plain-language lines for a human reader.</summary>
internal sealed record ServerExplanation(
    string Name,
    string Transport,
    string Target,
    string? Error,
    IReadOnlyList<string> Lines);

/// <summary>Output of <c>mcplense explain</c>: a human-readable "what is this MCP" summary.</summary>
internal sealed record ExplainReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ServerExplanation> Servers);

/// <summary>
/// Turns a fact-only <see cref="ScanReport"/> (plus its findings) into a short narrative a newcomer
/// can read: identity, auth posture, what it exposes, which tools look high-impact, and a one-line
/// findings summary. Pure - it only rearranges scan facts into sentences.
/// </summary>
internal static class ExplainBuilder
{
    public static ExplainReport Build(ScanReport scan, FindingsReport findings)
    {
        var byTarget = findings.Servers
            .GroupBy(s => s.Target, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var servers = scan.Servers
            .Select(s => Explain(s, byTarget.TryGetValue(s.Target, out var f) ? f : null))
            .ToList();
        return new ExplainReport(DateTimeOffset.UtcNow, servers);
    }

    private static ServerExplanation Explain(ServerScanResult server, ServerFindings? findings)
    {
        if (server.Error is not null)
        {
            return new ServerExplanation(server.Name, server.Transport, server.Target, server.Error,
                [$"Could not inspect this server: {server.Error}"]);
        }

        var lines = new List<string>();

        var info = server.Check("serverInfo");
        var name = info.Str("name") ?? server.Name;
        var version = info.Str("version");
        lines.Add(version is null ? $"{name} ({server.Transport})." : $"{name} v{version} ({server.Transport}).");
        if (info.Str("description") is { Length: > 0 } description)
        {
            lines.Add(Truncate(description.ReplaceLineEndings(" ").Trim(), 220));
        }

        lines.Add("Auth: " + AuthPhrase(server.Check("auth").Str("classification")));

        var tools = server.ToolItems().ToList();
        var destructive = ToolsWithHint(tools, "destructiveHint");
        var openWorld = ToolsWithHint(tools, "openWorldHint");
        var resourceCount = server.Check("resources").Array("items")?.Count ?? 0;
        var promptCount = server.Check("prompts").Array("items")?.Count ?? 0;
        lines.Add($"Exposes {tools.Count} tool(s), {resourceCount} resource(s), {promptCount} prompt(s).");

        if (destructive.Count > 0)
        {
            lines.Add($"Server-declared destructive tools: {string.Join(", ", destructive)}.");
        }

        if (openWorld.Count > 0)
        {
            lines.Add($"Open-world tools (reach beyond the server): {string.Join(", ", openWorld)}.");
        }

        if (findings is { Findings.Count: > 0 })
        {
            var counts = findings.Findings
                .GroupBy(f => f.Severity)
                .OrderByDescending(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key.ToWire()}");
            lines.Add($"Findings: {findings.Findings.Count} ({string.Join(", ", counts)}) - run 'mcplense analyze' for detail.");
        }
        else
        {
            lines.Add("Findings: none from the built-in rules.");
        }

        return new ServerExplanation(server.Name, server.Transport, server.Target, null, lines);
    }

    private static IReadOnlyList<string> ToolsWithHint(IEnumerable<System.Text.Json.Nodes.JsonNode> tools, string hint)
        => tools
            .Where(t => t["annotations"].Bool(hint) == true)
            .Select(t => t.Str("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

    private static string AuthPhrase(string? classification) => classification switch
    {
        "anonymous" => "anonymous (no credentials required - anyone who can reach it can use it).",
        "oauth-rfc9728" => "OAuth, advertised via RFC 9728 protected-resource metadata.",
        "oauth-bearer-unannounced" => "Bearer token required, but no RFC 9728 metadata is advertised.",
        "auth-required-unspecified" => "authentication required (the scheme could not be classified).",
        "stdio" => "stdio (local process; HTTP auth does not apply).",
        _ => "could not be determined."
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
