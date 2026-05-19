using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.Scanning;
using McpLense.Scanning.TargetResolution;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests;

/// <summary>
/// End-to-end integration tests for per-target headers (the <c>targets</c> /
/// <c>targetPatterns</c> overlay). Boots the 'headers' mode test MCP and asserts which
/// inbound HTTP requests carried which headers across the scan.
/// </summary>
public class TargetHeaderOverlayTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private string _baseUrl = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _app = await McpLense.TestMcps.Program.StartAsync("headers");
        _baseUrl = _app.Urls.First();
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task ClearCaptureAsync()
    {
        var response = await _http.PostAsync("/capture/clear", content: null);
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<CapturedRequestSnapshot>> SnapshotAsync()
    {
        var json = await _http.GetFromJsonAsync<JsonElement>("/capture");
        var requests = json.GetProperty("requests");
        var result = new List<CapturedRequestSnapshot>();
        foreach (var item in requests.EnumerateArray())
        {
            var method = item.GetProperty("method").GetString()!;
            var path = item.GetProperty("path").GetString()!;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in item.GetProperty("headers").EnumerateObject())
            {
                headers[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
            result.Add(new CapturedRequestSnapshot(method, path, headers));
        }
        return result;
    }

    private sealed record CapturedRequestSnapshot(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    private static TargetOptions BareTargetOptions(string url, IReadOnlyDictionary<string, string>? headers = null)
        => new(
            ConfigPaths: Array.Empty<string>(),
            ServerNames: Array.Empty<string>(),
            ProfilePaths: Array.Empty<string>(),
            DisplayName: null,
            Url: new Uri(url),
            Transport: TransportPreference.Auto,
            Headers: headers ?? new Dictionary<string, string>(),
            Command: null,
            CommandArguments: Array.Empty<string>(),
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

    private static TargetOptions TargetOptionsForConfig(string url, string configPath)
        => new(
            ConfigPaths: Array.Empty<string>(),
            ServerNames: Array.Empty<string>(),
            ProfilePaths: new[] { configPath },
            DisplayName: null,
            Url: new Uri(url),
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Command: null,
            CommandArguments: Array.Empty<string>(),
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

    [Fact]
    public async Task Cli_header_reaches_cors_preflight_by_default()
    {
        // CLI --header sends to ALL outbound calls when no per-target scope is set
        // (default scope is `all`). OPTIONS is the cleanest "this came from a probe"
        // assertion since only the scan pipeline ever issues OPTIONS against the URL.
        await ClearCaptureAsync();

        var target = BareTargetOptions(_baseUrl, new Dictionary<string, string>
        {
            ["x-mcp-ec-organization"] = "from-cli-flag"
        });

        await ScanCommandDispatcher.RunAsync(
            target,
            handshakeTimeout: TimeSpan.FromSeconds(15),
            cliEnables: null,
            cliDisables: null,
            CancellationToken.None,
            quiet: true);

        var captured = await SnapshotAsync();
        captured.Count.ShouldBeGreaterThan(1, "expected multiple requests (session POST + probes)");

        var optionsRequests = captured.Where(r => r.Method == "OPTIONS").ToList();
        optionsRequests.Count.ShouldBeGreaterThan(0);
        optionsRequests
            .All(r => r.Headers.TryGetValue("x-mcp-ec-organization", out var v) && v == "from-cli-flag")
            .ShouldBeTrue("CLI --header should ride the CORS preflight by default (scope=all)");
    }

    [Fact]
    public async Task Config_target_header_with_scope_all_reaches_cors_preflight()
    {
        await ClearCaptureAsync();

        var configContent = $$"""
        {
          "targets": [
            {
              "name": "test-server",
              "url":  "{{_baseUrl}}",
              "headers": {
                "x-mcp-ec-organization": "from-config",
                "x-mcp-ec-project":      "myproj"
              },
              "scope": "All"
            }
          ]
        }
        """;
        var configPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(configPath, configContent);
        try
        {
            var target = TargetOptionsForConfig(_baseUrl, configPath);

            await ScanCommandDispatcher.RunAsync(
                target,
                handshakeTimeout: TimeSpan.FromSeconds(15),
                cliEnables: null,
                cliDisables: null,
                CancellationToken.None,
                quiet: true);

            var captured = await SnapshotAsync();
            captured.Count.ShouldBeGreaterThan(0);

            // CORS preflight (OPTIONS) is the cleanest "probe-only" signal: only the scan
            // pipeline ever issues OPTIONS against the MCP URL. Under scope=all it MUST carry
            // the headers - this is the gap-coverage assertion the user asked for
            // ("some servers gate everything").
            var optionsRequests = captured.Where(r => r.Method == "OPTIONS").ToList();
            optionsRequests.Count.ShouldBeGreaterThan(0, "expected at least one CORS preflight OPTIONS");
            optionsRequests
                .All(r => r.Headers.TryGetValue("x-mcp-ec-organization", out var v) && v == "from-config")
                .ShouldBeTrue("scope=all must extend the per-target header to the CORS preflight");
            optionsRequests
                .All(r => r.Headers.TryGetValue("x-mcp-ec-project", out var v) && v == "myproj")
                .ShouldBeTrue("scope=all must extend EVERY per-target header to the CORS preflight");
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task Config_target_header_with_scope_session_keeps_cors_preflight_bare()
    {
        await ClearCaptureAsync();

        var configContent = $$"""
        {
          "targets": [
            {
              "name": "test-server",
              "url":  "{{_baseUrl}}",
              "headers": { "x-mcp-ec-organization": "session-only" },
              "scope": "Session"
            }
          ]
        }
        """;
        var configPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(configPath, configContent);
        try
        {
            var target = TargetOptionsForConfig(_baseUrl, configPath);

            await ScanCommandDispatcher.RunAsync(
                target,
                handshakeTimeout: TimeSpan.FromSeconds(15),
                cliEnables: null,
                cliDisables: null,
                CancellationToken.None,
                quiet: true);

            var captured = await SnapshotAsync();
            captured.Count.ShouldBeGreaterThan(0);

            // With scope=session the CORS preflight must NOT carry the per-target header.
            // (The transport-probe GET shares the URL/path with the MCP session's SSE GET,
            // so distinguishing the two on GET alone is fragile - OPTIONS is unambiguous.)
            var optionsRequests = captured.Where(r => r.Method == "OPTIONS").ToList();
            optionsRequests.Count.ShouldBeGreaterThan(0, "expected at least one CORS preflight OPTIONS");
            optionsRequests
                .All(r => !r.Headers.ContainsKey("x-mcp-ec-organization"))
                .ShouldBeTrue("scope=session must keep OPTIONS preflight header-free");
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task Tools_command_through_McpExecutor_carries_per_target_headers_to_session()
    {
        // Regression test for the original bug: non-scan commands (tools, inspect, etc.)
        // bypassed the per-target overlay and sent requests bare. McpExecutor now resolves
        // the same overlay before opening the MCP session.
        await ClearCaptureAsync();

        var configContent = $$"""
        {
          "targets": [
            {
              "name":   "test-server",
              "url":    "{{_baseUrl}}",
              "headers": { "x-mcp-ec-organization": "tools-via-executor" },
              "scope":   "All"
            }
          ]
        }
        """;
        var configPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(configPath, configContent);
        try
        {
            // Drive the McpExecutor path explicitly (the same code `mcplense tools` calls).
            // The test MCP doesn't need auth; use --no-auth to skip the profile-resolution
            // dance entirely (the config file only carries `targets`, no `authProfiles`).
            var parsed = new ParsedCommand(
                Command: AppCommand.Tools,
                Subject: null,
                Arguments: null,
                Format: OutputFormat.Json,
                Timeout: TimeSpan.FromSeconds(15),
                Target: new TargetOptions(
                    ConfigPaths: Array.Empty<string>(),
                    ServerNames: Array.Empty<string>(),
                    ProfilePaths: new[] { configPath },
                    DisplayName: null,
                    Url: new Uri(_baseUrl),
                    Transport: TransportPreference.Auto,
                    Headers: new Dictionary<string, string>(),
                    Command: null,
                    CommandArguments: Array.Empty<string>(),
                    WorkingDirectory: null,
                    Environment: new Dictionary<string, string>(),
                    AuthOverrides: new AuthOverrides(NoAuth: true)),
                ProgressEnabled: false,
                Quiet: true,
                Verbose: false);

            await McpExecutor.ExecuteAsync(
                parsed,
                new System.Text.Json.JsonSerializerOptions(),
                CancellationToken.None);

            var captured = await SnapshotAsync();

            // The MCP session uses POST. Confirm at least one POST carried the header.
            var postWithHeader = captured.Count(r => r.Method == "POST"
                && r.Headers.TryGetValue("x-mcp-ec-organization", out var v)
                && v == "tools-via-executor");
            postWithHeader.ShouldBeGreaterThan(0, "tools command must propagate per-target headers to the MCP session");
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
