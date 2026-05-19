using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.Scanning;
using McpLense.Scanning.TargetResolution;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning.TargetResolution;

/// <summary>
/// Verifies the `bluebird` wildcard pattern shipped in the user's personal
/// McpLense.Profiles.json matches the expected URL. Acts as a regression guard for the
/// pattern grammar so a future glob refactor can't quietly stop matching the user's
/// declared targets.
/// </summary>
public class BluebirdPatternMatchTests
{
    private static async Task<ScanConfig> LoadInlineAsync(string content)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, content);
        try
        {
            return await ScanConfigLoader.LoadAsync(new[] { path }, CancellationToken.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("https://mcp.bluebird-ai.net/", true)]
    [InlineData("https://mcp.bluebird-ai.net/mcp", true)]
    [InlineData("https://bluebird.example.com/x", true)]
    [InlineData("https://something.bluebird-internal.corp/api/v1", true)]
    [InlineData("https://example.com/mcp", false)] // no 'bluebird' in host or path
    public async Task BluebirdPattern_matches_expected_urls(string url, bool expectMatch)
    {
        const string ConfigJson = """
        {
          "targetPatterns": [
            {
              "match":   "https://**bluebird**/**",
              "headers": {
                "x-mcp-ec-organization": "msazure",
                "x-mcp-ec-project":      "One",
                "x-mcp-ec-repository":   "ZTS"
              },
              "scope":   "All"
            }
          ]
        }
        """;

        var config = await LoadInlineAsync(ConfigJson);
        config.TargetPatterns.Count.ShouldBe(1);

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri(url),
            namedReference: null,
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        if (expectMatch)
        {
            overlay.MatchedPatterns.Count.ShouldBe(1);
            overlay.Headers["x-mcp-ec-organization"].ShouldBe("msazure");
            overlay.Headers["x-mcp-ec-project"].ShouldBe("One");
            overlay.Headers["x-mcp-ec-repository"].ShouldBe("ZTS");
            overlay.Scope.ShouldBe(TargetScope.All);
        }
        else
        {
            overlay.MatchedPatterns.Count.ShouldBe(0);
            overlay.Headers.Count.ShouldBe(0);
        }
    }
}
