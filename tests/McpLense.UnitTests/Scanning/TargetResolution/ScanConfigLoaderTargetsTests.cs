using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.Scanning;
using McpLense.Scanning.TargetResolution;
using McpLense.UnitTests.Helpers;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning.TargetResolution;

public class ScanConfigLoaderTargetsTests
{
    private static async Task<ScanConfig> LoadFromTextAsync(string contents)
    {
        using var temp = new TempFile(contents);
        return await ScanConfigLoader.LoadAsync(new[] { temp.Path }, CancellationToken.None);
    }

    [Fact]
    public async Task Top_level_targets_block_is_loaded()
    {
        const string Json = """
        {
          "targets": [
            {
              "name": "ec-foo",
              "url":  "https://example.ec.com/foo/mcp",
              "headers": {
                "x-mcp-ec-organization": "myorg",
                "x-mcp-ec-project":      "myproj"
              }
            }
          ]
        }
        """;

        var config = await LoadFromTextAsync(Json);
        config.Targets.Count.ShouldBe(1);
        var entry = config.Targets[0];
        entry.Name.ShouldBe("ec-foo");
        entry.Url.ShouldBe("https://example.ec.com/foo/mcp");
        entry.Headers!["x-mcp-ec-organization"].ShouldBe("myorg");
        entry.Headers["x-mcp-ec-project"].ShouldBe("myproj");
    }

    [Fact]
    public async Task Top_level_target_patterns_block_is_loaded_with_defaults()
    {
        const string Json = """
        {
          "targetPatterns": [
            {
              "match":   "https://*.ec.com/**",
              "headers": { "x-mcp-ec-organization": "default-org" },
              "scope":   "All"
            }
          ]
        }
        """;

        var config = await LoadFromTextAsync(Json);
        config.TargetPatterns.Count.ShouldBe(1);
        var pat = config.TargetPatterns[0];
        pat.Match.ShouldBe("https://*.ec.com/**");
        pat.Headers!["x-mcp-ec-organization"].ShouldBe("default-org");
        pat.Scope.ShouldBe(TargetScope.All);
    }

    [Fact]
    public async Task Header_values_are_environment_expanded()
    {
        var prevKey = System.Environment.GetEnvironmentVariable("MCPLENSE_TEST_HEADER_VAR");
        System.Environment.SetEnvironmentVariable("MCPLENSE_TEST_HEADER_VAR", "from-env");
        try
        {
            const string Json = """
            {
              "targets": [
                {
                  "name": "x",
                  "url":  "https://x.example.com/mcp",
                  "headers": {
                    "x-from-env": "${MCPLENSE_TEST_HEADER_VAR}",
                    "x-default":  "${MCPLENSE_TEST_HEADER_NOT_SET:-fallback-value}"
                  }
                }
              ]
            }
            """;

            var config = await LoadFromTextAsync(Json);
            var entry = config.Targets[0];
            entry.Headers!["x-from-env"].ShouldBe("from-env");
            entry.Headers["x-default"].ShouldBe("fallback-value");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("MCPLENSE_TEST_HEADER_VAR", prevKey);
        }
    }

    [Fact]
    public async Task Malformed_pattern_is_skipped_with_stderr_warning_not_a_hard_error()
    {
        // 'no-scheme' is not a valid pattern - the loader logs a warning and skips it
        // rather than failing the whole load (other patterns + targets must keep working).
        const string Json = """
        {
          "targetPatterns": [
            { "match": "no-scheme", "headers": { "x": "1" } },
            { "match": "https://valid.example.com/**", "headers": { "y": "2" } }
          ]
        }
        """;

        var config = await LoadFromTextAsync(Json);
        config.TargetPatterns.Count.ShouldBe(1);
        config.TargetPatterns[0].Match.ShouldBe("https://valid.example.com/**");
    }

    [Fact]
    public async Task Top_level_block_coexists_with_authProfiles_only_file()
    {
        // A file that has authProfiles AND targets should still parse - we don't require
        // the scan block to be present.
        const string Json = """
        {
          "authProfiles": [
            { "name": "demo", "auth": { "type": "bearer", "token": "tok" } }
          ],
          "targets": [
            { "name": "x", "url": "https://x.example.com/mcp" }
          ]
        }
        """;

        var config = await LoadFromTextAsync(Json);
        config.Targets.Count.ShouldBe(1);
        config.Targets[0].Name.ShouldBe("x");
    }

    [Fact]
    public async Task Disabled_checks_are_loaded_as_string_array()
    {
        const string Json = """
        {
          "targets": [
            {
              "name": "x",
              "url":  "https://x.example.com/mcp",
              "disabledChecks": ["corsPreflight", "tlsChain"]
            }
          ]
        }
        """;

        var config = await LoadFromTextAsync(Json);
        config.Targets[0].DisabledChecks!.ShouldBe(new[] { "corsPreflight", "tlsChain" });
    }
}
