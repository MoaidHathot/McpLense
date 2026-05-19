using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McpLense;
using McpLense.Scanning;
using McpLense.Scanning.TargetResolution;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning.TargetResolution;

/// <summary>
/// Verifies the verbose stderr emitted by <see cref="TargetOverlayApplicator"/> when an
/// overlay matches: identifier headers print verbatim so users can verify their values;
/// secret-shaped headers print as <c>&lt;redacted, length=N&gt;</c> so a recording of the
/// terminal doesn't leak tokens.
/// </summary>
public class TargetOverlayApplicatorTraceTests
{
    private static (IReadOnlyList<ResolvedServer> Servers, string Stderr) Apply(
        IReadOnlyDictionary<string, string> headers,
        bool verbose)
    {
        var config = new ScanConfig();
        config.Targets.Add(new ScanTargetEntry
        {
            Name = "test-target",
            Url = "https://example.com/mcp",
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
        });

        var server = new ResolvedServer(
            Name: "example",
            Kind: ConnectionKind.Http,
            Target: "https://example.com/mcp",
            Source: "test",
            Command: null,
            CommandArguments: Array.Empty<string>(),
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: new Uri("https://example.com/mcp"),
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>());

        var target = new TargetOptions(
            ConfigPaths: Array.Empty<string>(),
            ServerNames: Array.Empty<string>(),
            ProfilePaths: Array.Empty<string>(),
            DisplayName: null,
            Url: server.Url,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Command: null,
            CommandArguments: Array.Empty<string>(),
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

        using var stderr = new StringWriter();
        var overlaid = TargetOverlayApplicator.Apply(
            new[] { server },
            config,
            target,
            cliDisables: null,
            quiet: false,
            verbose: verbose,
            stderr: stderr);

        return (overlaid, stderr.ToString());
    }

    [Fact]
    public void Identifier_header_values_are_printed_verbatim_under_verbose()
    {
        var (_, stderr) = Apply(
            new Dictionary<string, string>
            {
                ["x-mcp-ec-organization"] = "msazure",
                ["x-mcp-ec-project"] = "One",
                ["x-mcp-ec-repository"] = "ZTS"
            },
            verbose: true);

        stderr.ShouldContain("x-mcp-ec-organization: msazure");
        stderr.ShouldContain("x-mcp-ec-project: One");
        stderr.ShouldContain("x-mcp-ec-repository: ZTS");
    }

    [Fact]
    public void Sensitive_header_values_are_redacted_under_verbose()
    {
        var (_, stderr) = Apply(
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer eyJhbGciOiJSUzI1NiJ9.payload.signature",
                ["x-api-key"] = "sk-live-secret-token",
                ["x-some-secret"] = "hunter2",
                ["cookie"] = "session=abc",
                ["x-mcp-ec-organization"] = "msazure"  // not sensitive
            },
            verbose: true);

        stderr.ShouldNotContain("Bearer eyJhbGciOiJSUzI1NiJ9.payload.signature");
        stderr.ShouldNotContain("sk-live-secret-token");
        stderr.ShouldNotContain("hunter2");
        stderr.ShouldNotContain("session=abc");

        // Each redacted header carries a "<redacted, length=N>" marker so a debugger can
        // at least sanity-check the value length without leaking the contents.
        var redactionCount = System.Text.RegularExpressions.Regex
            .Matches(stderr, @"<redacted, length=\d+>")
            .Count;
        redactionCount.ShouldBe(4); // Authorization, x-api-key, x-some-secret, cookie

        // Identifier header survives unscathed.
        stderr.ShouldContain("x-mcp-ec-organization: msazure");
    }

    [Fact]
    public void Non_verbose_summary_does_not_print_header_lines()
    {
        var (_, stderr) = Apply(
            new Dictionary<string, string>
            {
                ["x-mcp-ec-organization"] = "msazure"
            },
            verbose: false);

        stderr.ShouldContain("matched:");
        stderr.ShouldNotContain("x-mcp-ec-organization:"); // not under default mode
    }
}
