using System.Text;
using System.Text.Json;
using McpLense.Analysis;
using McpLense.Learning;

namespace McpLense;

/// <summary>
/// Renders the shareable/readable payloads (explain, findings, inspect) as Markdown. Anything else
/// falls back to the plain-text renderer inside a fenced code block, so the output is always valid
/// Markdown.
/// </summary>
internal static class MarkdownRenderer
{
    public static string Render(object payload, JsonSerializerOptions jsonOptions) => payload switch
    {
        ExplainReport explain => RenderExplain(explain),
        FindingsReport findings => RenderFindings(findings),
        AnalyzedScanReport analyzed => RenderFindings(analyzed.Findings),
        InspectReport inspect => RenderInspect(inspect),
        _ => "```\n" + TextFormatter.Format(payload, jsonOptions) + "\n```"
    };

    private static string RenderExplain(ExplainReport report)
    {
        var sb = new StringBuilder();
        foreach (var server in report.Servers)
        {
            if (server.Lines.Count == 0)
            {
                continue;
            }

            sb.AppendLine($"## {server.Lines[0]}").AppendLine();
            for (var i = 1; i < server.Lines.Count; i++)
            {
                sb.AppendLine($"- {server.Lines[i]}");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderFindings(FindingsReport report)
    {
        var sb = new StringBuilder();
        foreach (var server in report.Servers)
        {
            sb.AppendLine($"## Findings — {server.Target}").AppendLine();
            if (server.Findings.Count == 0)
            {
                sb.AppendLine("No findings from the built-in rules.").AppendLine();
                continue;
            }

            sb.AppendLine("| Severity | Rule | Finding | Evidence |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var f in server.Findings)
            {
                sb.AppendLine($"| {f.Severity.ToWire()} | `{f.RuleId}` | {Escape(f.Title)} | `{Escape(f.EvidencePath)}` |");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderInspect(InspectReport report)
    {
        var sb = new StringBuilder();
        foreach (var server in report.Servers)
        {
            sb.AppendLine($"# {server.Name}").AppendLine();
            sb.AppendLine($"- **Target:** {server.Target}");
            sb.AppendLine($"- **Transport:** {server.Transport}");
            if (server.Error is not null)
            {
                sb.AppendLine($"- **Error:** {Escape(server.Error)}").AppendLine();
                continue;
            }

            sb.AppendLine();
            Section(sb, "Tools", server.Tools.Items.Select(t => (t.Name, t.Description)));
            Section(sb, "Resources", server.Resources.Items.Select(r => (r.Uri ?? r.Name ?? "(unnamed)", r.Description)));
            Section(sb, "Prompts", server.Prompts.Items.Select(p => (p.Name, p.Description)));
        }

        return sb.ToString().TrimEnd();
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<(string Name, string? Description)> items)
    {
        var list = items.ToList();
        sb.AppendLine($"## {title} ({list.Count})").AppendLine();
        if (list.Count == 0)
        {
            sb.AppendLine("_none_").AppendLine();
            return;
        }

        sb.AppendLine("| Name | Description |");
        sb.AppendLine("|---|---|");
        foreach (var (name, description) in list)
        {
            sb.AppendLine($"| `{Escape(name)}` | {Escape(Collapse(description))} |");
        }

        sb.AppendLine();
    }

    private static string Collapse(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : text.ReplaceLineEndings(" ").Trim();

    private static string Escape(string? text)
        => (text ?? string.Empty).Replace("|", "\\|");
}
