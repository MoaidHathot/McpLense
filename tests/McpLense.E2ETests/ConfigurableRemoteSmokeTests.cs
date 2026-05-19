using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace McpLense.E2ETests;

/// <summary>
/// Live remote-MCP smoke tests driven by <c>remote-targets.json</c>. Every entry in that
/// file produces one CLI <c>scan</c> invocation and asserts against the documented
/// expectations. All tests are gated on the environment variable
/// <c>MCPLENSE_E2E_REMOTE</c> so default CI never fires public traffic.
/// </summary>
public class ConfigurableRemoteSmokeTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    public static IEnumerable<object[]> RemoteTargets()
    {
        var path = Path.Combine(BuildArtifacts.RepoRoot, "tests", "McpLense.E2ETests", "remote-targets.json");
        if (!File.Exists(path))
        {
            yield break;
        }

        var json = File.ReadAllText(path);
        var doc = System.Text.Json.JsonSerializer.Deserialize<RemoteTargetsFile>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (doc?.Targets is null)
        {
            yield break;
        }

        foreach (var target in doc.Targets)
        {
            if (target is null || string.IsNullOrEmpty(target.Url))
            {
                continue;
            }
            yield return new object[] { target };
        }
    }

    [SkipUnlessEnvTheory("MCPLENSE_E2E_REMOTE")]
    [MemberData(nameof(RemoteTargets))]
    public async Task Scan_RemoteTarget_MeetsExpectations(RemoteTarget target)
    {
        var result = await CliRunner.RunAsync([
            "scan",
            target.Url,
            "--format", "json",
            "--timeout", "60",
            "--quiet"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0, $"stderr=<<{result.StandardError}>>");

        // Top-level scan envelope present.
        result.StandardOutput.ShouldContain("\"servers\":");
        result.StandardOutput.ShouldContain("\"checks\":");

        if (target.Expectations is null)
        {
            return;
        }

        if (target.Expectations.AuthClassification is { } expected)
        {
            // Loose contains check: avoids reparsing the full JSON in the smoke test.
            result.StandardOutput.ShouldContain($"\"classification\": \"{expected}\"");
        }

        if (target.Expectations.ExpectToolsListFetched == true)
        {
            // 'tools' check sets "fetched": true when reachable.
            result.StandardOutput.ShouldContain("\"fetched\": true");
        }
    }

    public sealed class RemoteTargetsFile
    {
        [JsonPropertyName("targets")] public RemoteTarget[]? Targets { get; set; }
    }

    public sealed class RemoteTarget
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("expectations")] public RemoteExpectations? Expectations { get; set; }
        public override string ToString() => $"{Name} ({Url})";
    }

    public sealed class RemoteExpectations
    {
        [JsonPropertyName("authClassification")] public string? AuthClassification { get; set; }
        [JsonPropertyName("expectToolsListFetched")] public bool? ExpectToolsListFetched { get; set; }
        [JsonPropertyName("minToolCount")] public int? MinToolCount { get; set; }
    }
}
