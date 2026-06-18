using System;
using System.Text.Json;
using McpLense;
using McpLense.Analysis;
using McpLense.Learning;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Output;

public class MarkdownRendererTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static string Render(object payload) => OutputRenderer.Render(OutputFormat.Markdown, payload, JsonOptions);

    [Fact]
    public void Explain_RendersHeadingAndBullets()
    {
        var report = new ExplainReport(DateTimeOffset.UnixEpoch,
            [new ServerExplanation("s", "http", "https://x/mcp", null, ["Server X v1 (http).", "Auth: anonymous.", "Exposes 2 tool(s)."])]);

        var md = Render(report);

        md.ShouldContain("## Server X v1 (http).");
        md.ShouldContain("- Auth: anonymous.");
        md.ShouldContain("- Exposes 2 tool(s).");
    }

    [Fact]
    public void Findings_RendersTable()
    {
        var report = new FindingsReport(DateTimeOffset.UnixEpoch,
            [new ServerFindings("s", "https://x/mcp", [new Finding("mixed-content", Severity.High, "Plain HTTP", "checks.transport", "true", "use https")])]);

        var md = Render(report);

        md.ShouldContain("## Findings — https://x/mcp");
        md.ShouldContain("| Severity | Rule | Finding | Evidence |");
        md.ShouldContain("| high | `mixed-content` | Plain HTTP |");
    }

    [Fact]
    public void UnknownPayload_FallsBackToFencedText()
    {
        var md = Render(new { hello = "world" });
        md.ShouldStartWith("```");
        md.ShouldEndWith("```");
    }
}
