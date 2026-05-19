using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Pure-counts metrics over every text field in the report. No labelling - counts only:
/// char length, line count, URL count, URL list (verbatim, where configured),
/// markdown link / image count, code-block fence count, non-ASCII / control-char counts.
/// Applied to a configurable set of fields; defaults to server instructions, tool
/// descriptions, prompt descriptions.
/// </summary>
internal sealed class MetricsCheck : IScanCheck
{
    public string Id => "metrics";

    /// <summary>
    /// Runs last; reads prior outputs to fold metrics into a flat per-field roll-up.
    /// </summary>
    public IReadOnlyList<string> DependsOn => new[] { "tools", "prompts", "resources", "protocol" };
    public bool IsEnabledByDefault => true;

    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRegex = new(@"\[[^\]]+\]\([^\)]+\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownImageRegex = new(@"!\[[^\]]*\]\([^\)]+\)", RegexOptions.Compiled);
    private static readonly Regex CodeBlockRegex = new("```", RegexOptions.Compiled);

    public Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var fields = ResolveExtractionFields(context.Config.GetCheckConfig(Id));
        var entries = new List<TextMetric>();

        if (fields.Contains("serverInstructions"))
        {
            var instructions = (context.GetPriorOutput("protocol") as JsonObject)?["instructions"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(instructions))
            {
                entries.Add(MeasureField("serverInstructions", instructions));
            }
        }

        if (fields.Contains("toolDescription"))
        {
            var tools = (context.GetPriorOutput("tools") as JsonObject)?["items"] as JsonArray;
            if (tools is not null)
            {
                foreach (var item in tools.OfType<JsonObject>())
                {
                    var name = item["name"]?.GetValue<string>() ?? "(unnamed)";
                    var desc = item["description"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(desc))
                    {
                        entries.Add(MeasureField($"tool:{name}:description", desc));
                    }
                }
            }
        }

        if (fields.Contains("promptDescription"))
        {
            var prompts = (context.GetPriorOutput("prompts") as JsonObject)?["items"] as JsonArray;
            if (prompts is not null)
            {
                foreach (var item in prompts.OfType<JsonObject>())
                {
                    var name = item["name"]?.GetValue<string>() ?? "(unnamed)";
                    var desc = item["description"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(desc))
                    {
                        entries.Add(MeasureField($"prompt:{name}:description", desc));
                    }
                }
            }
        }

        var data = new MetricsData(
            ExtractionFields: fields,
            Fields: entries);

        return Task.FromResult(new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(data), Error: null));
    }

    private static TextMetric MeasureField(string path, string text)
    {
        var urls = UrlRegex.Matches(text).Select(m => m.Value).ToArray();
        var nonAscii = 0;
        var control = 0;
        var tabs = 0;
        foreach (var ch in text)
        {
            if (ch > 127) nonAscii++;
            if (char.IsControl(ch) && ch != '\n' && ch != '\r' && ch != '\t') control++;
            if (ch == '\t') tabs++;
        }

        return new TextMetric(
            Path: path,
            CharLength: text.Length,
            LineCount: text.Count(c => c == '\n') + 1,
            UrlCount: urls.Length,
            Urls: urls,
            MarkdownLinkCount: MarkdownLinkRegex.Matches(text).Count,
            MarkdownImageCount: MarkdownImageRegex.Matches(text).Count,
            CodeBlockFenceCount: CodeBlockRegex.Matches(text).Count,
            NonAsciiCharCount: nonAscii,
            ControlCharCount: control,
            TabCount: tabs);
    }

    private static IReadOnlySet<string> ResolveExtractionFields(JsonObject? config)
    {
        var defaults = new[] { "serverInstructions", "toolDescription", "promptDescription" };
        if (config?["urlExtractionFields"] is not JsonArray arr)
        {
            return defaults.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return arr.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record MetricsData(
        IReadOnlySet<string> ExtractionFields,
        IReadOnlyList<TextMetric> Fields);

    internal sealed record TextMetric(
        string Path,
        int CharLength,
        int LineCount,
        int UrlCount,
        IReadOnlyList<string> Urls,
        int MarkdownLinkCount,
        int MarkdownImageCount,
        int CodeBlockFenceCount,
        int NonAsciiCharCount,
        int ControlCharCount,
        int TabCount);
}
