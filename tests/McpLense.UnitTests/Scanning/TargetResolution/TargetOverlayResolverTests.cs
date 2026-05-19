using System;
using System.Collections.Generic;
using McpLense;
using McpLense.Scanning;
using McpLense.Scanning.TargetResolution;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning.TargetResolution;

public class TargetOverlayResolverTests
{
    private static ScanConfig ConfigWith(
        IReadOnlyList<ScanTargetEntry>? targets = null,
        IReadOnlyList<TargetPatternEntry>? patterns = null)
    {
        var config = new ScanConfig();
        if (targets is not null)
        {
            foreach (var t in targets) { config.Targets.Add(t); }
        }
        if (patterns is not null)
        {
            foreach (var p in patterns) { config.TargetPatterns.Add(p); }
        }
        return config;
    }

    [Fact]
    public void Empty_config_yields_empty_overlay()
    {
        var overlay = TargetOverlayResolver.Resolve(
            new ScanConfig(),
            new Uri("https://example.com/mcp"),
            namedReference: null,
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        overlay.Headers.Count.ShouldBe(0);
        overlay.Profile.ShouldBeNull();
        overlay.Transport.ShouldBeNull();
        overlay.MatchedPatterns.Count.ShouldBe(0);
        overlay.MatchedTargetName.ShouldBeNull();
        overlay.HasAny.ShouldBeFalse();
    }

    [Fact]
    public void Target_entry_matches_by_exact_url_auto()
    {
        var config = ConfigWith(new[]
        {
            new ScanTargetEntry
            {
                Name = "ec-foo",
                Url = "https://example.ec.com/foo/mcp",
                Headers = new Dictionary<string, string>
                {
                    ["x-mcp-ec-organization"] = "myorg"
                }
            }
        });

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri("https://example.ec.com/foo/mcp"),
            namedReference: null,
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        overlay.MatchedTargetName.ShouldBe("ec-foo");
        overlay.Headers["x-mcp-ec-organization"].ShouldBe("myorg");
        overlay.Scope.ShouldBe(TargetScope.All); // default
    }

    [Fact]
    public void Target_entry_matches_by_named_reference()
    {
        var config = ConfigWith(new[]
        {
            new ScanTargetEntry
            {
                Name = "ec-foo",
                Url = "https://example.ec.com/foo/mcp",
                Headers = new Dictionary<string, string> { ["x-org"] = "myorg" }
            }
        });

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri("https://example.ec.com/foo/mcp"),
            namedReference: "ec-foo",
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        overlay.MatchedTargetName.ShouldBe("ec-foo");
        overlay.Headers["x-org"].ShouldBe("myorg");
    }

    [Fact]
    public void Patterns_apply_in_declaration_order_last_writes_wins_per_key()
    {
        var config = ConfigWith(patterns: new[]
        {
            new TargetPatternEntry
            {
                Match = "https://*.example.com/**",
                Headers = new Dictionary<string, string>
                {
                    ["x-a"] = "1",
                    ["x-shared"] = "from-first-pattern"
                }
            },
            new TargetPatternEntry
            {
                Match = "https://api.example.com/**",
                Headers = new Dictionary<string, string>
                {
                    ["x-b"] = "2",
                    ["x-shared"] = "from-second-pattern"
                }
            }
        });

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri("https://api.example.com/mcp"),
            namedReference: null,
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        overlay.MatchedPatterns.Count.ShouldBe(2);
        overlay.Headers["x-a"].ShouldBe("1");
        overlay.Headers["x-b"].ShouldBe("2");
        overlay.Headers["x-shared"].ShouldBe("from-second-pattern");
    }

    [Fact]
    public void Target_entry_overrides_pattern_headers()
    {
        var config = ConfigWith(
            targets: new[]
            {
                new ScanTargetEntry
                {
                    Url = "https://api.example.com/mcp",
                    Headers = new Dictionary<string, string> { ["x-key"] = "target-wins" }
                }
            },
            patterns: new[]
            {
                new TargetPatternEntry
                {
                    Match = "https://*.example.com/**",
                    Headers = new Dictionary<string, string> { ["x-key"] = "pattern-loses" }
                }
            });

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri("https://api.example.com/mcp"),
            namedReference: null,
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        overlay.Headers["x-key"].ShouldBe("target-wins");
    }

    [Fact]
    public void Cli_headers_win_over_target_and_pattern()
    {
        var config = ConfigWith(
            targets: new[]
            {
                new ScanTargetEntry
                {
                    Url = "https://api.example.com/mcp",
                    Headers = new Dictionary<string, string> { ["x-key"] = "from-target" }
                }
            },
            patterns: new[]
            {
                new TargetPatternEntry
                {
                    Match = "https://api.example.com/**",
                    Headers = new Dictionary<string, string> { ["x-key"] = "from-pattern" }
                }
            });

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri("https://api.example.com/mcp"),
            namedReference: null,
            cliHeaders: new Dictionary<string, string> { ["x-key"] = "from-cli" },
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        overlay.Headers["x-key"].ShouldBe("from-cli");
    }

    [Fact]
    public void Scope_session_overrides_default_all_on_per_target_basis()
    {
        var config = ConfigWith(new[]
        {
            new ScanTargetEntry
            {
                Url = "https://example.com/mcp",
                Scope = TargetScope.Session
            }
        });

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri("https://example.com/mcp"),
            namedReference: null,
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        overlay.Scope.ShouldBe(TargetScope.Session);
    }

    [Fact]
    public void Disabled_checks_union_pattern_target_and_cli()
    {
        var config = ConfigWith(
            targets: new[]
            {
                new ScanTargetEntry
                {
                    Url = "https://api.example.com/mcp",
                    DisabledChecks = new List<string> { "tlsChain" }
                }
            },
            patterns: new[]
            {
                new TargetPatternEntry
                {
                    Match = "https://**/**",
                    DisabledChecks = new List<string> { "corsPreflight" }
                }
            });

        var cliDisables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metrics" };

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri("https://api.example.com/mcp"),
            namedReference: null,
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: cliDisables);

        overlay.DisabledChecks.ShouldContain("tlsChain");
        overlay.DisabledChecks.ShouldContain("corsPreflight");
        overlay.DisabledChecks.ShouldContain("metrics");
    }

    [Fact]
    public void Url_match_normalises_trailing_slash()
    {
        var config = ConfigWith(new[]
        {
            new ScanTargetEntry
            {
                Name = "x",
                Url = "https://example.com/mcp/"
            }
        });

        var overlay = TargetOverlayResolver.Resolve(
            config,
            new Uri("https://example.com/mcp"),
            namedReference: null,
            cliHeaders: null,
            cliProfile: null,
            cliTransport: null,
            cliTimeout: null,
            cliDisables: null);

        overlay.MatchedTargetName.ShouldBe("x");
    }

    [Fact]
    public void Named_reference_lookup_returns_url()
    {
        var config = ConfigWith(new[]
        {
            new ScanTargetEntry { Name = "alpha", Url = "https://a.example.com/mcp" },
            new ScanTargetEntry { Name = "beta", Url = "https://b.example.com/mcp" }
        });

        TargetOverlayResolver.ResolveNamedTargetUrl(config, "alpha").ShouldBe("https://a.example.com/mcp");
        TargetOverlayResolver.ResolveNamedTargetUrl(config, "BETA").ShouldBe("https://b.example.com/mcp"); // case-insensitive
        TargetOverlayResolver.ResolveNamedTargetUrl(config, "missing").ShouldBeNull();
    }
}
