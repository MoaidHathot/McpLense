using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace McpLense.E2ETests;

[Collection("HttpTestServer")]
public class HttpCliE2ETests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpTestServerProcessFixture _fixture;

    public HttpCliE2ETests(HttpTestServerProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Tools_HttpAuto_AgainstHttpTestServer_ListsKnownTools()
    {
        var result = await CliRunner.RunAsync([
            "tools",
            "--url", _fixture.BaseUrl,
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"Echo\"");
        result.StandardOutput.ShouldContain("\"Add\"");
        result.StandardOutput.ShouldContain("\"GetHeader\"");
    }

    [Fact]
    public async Task Inspect_HttpStreamableHttp_AgainstHttpTestServer_ReturnsZero()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.BaseUrl,
            "--transport", "streamable-http",
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"servers\":");
        result.StandardOutput.ShouldContain("\"capabilities\":");
        result.StandardOutput.ShouldContain("\"Echo\"");
    }

    [Fact]
    public async Task Inspect_HttpSse_AgainstSseEndpoint_ReturnsZero()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", _fixture.SseUrl,
            "--transport", "sse",
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"servers\":");
        result.StandardOutput.ShouldContain("\"Echo\"");
    }

    [Fact]
    public async Task Call_GetHeader_WithHeaderFlag_ReturnsZeroAndHeaderValue()
    {
        var result = await CliRunner.RunAsync([
            "call", "GetHeader",
            "--url", _fixture.BaseUrl,
            "--header", "X-Mcplense-Test=hello-from-cli",
            "--args", "{\"name\":\"X-Mcplense-Test\"}",
            "--progress", "false",
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("hello-from-cli");
    }

    [Fact]
    public async Task Inspect_BadUrl_ReturnsOne()
    {
        var result = await CliRunner.RunAsync([
            "inspect",
            "--url", "http://127.0.0.1:1/",
            "--format", "json",
            "--timeout", "5"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Inspect_HttpUrlInConfigFile_ResolvesAndReturnsZero()
    {
        using var dir = new TempDirectory();
        var configPath = dir.WriteFile("mcp.json", $$"""
        {
          "mcpServers": {
            "http-fixture": {
              "url": "{{_fixture.BaseUrl}}"
            }
          }
        }
        """);

        var result = await CliRunner.RunAsync([
            "inspect",
            "--config", configPath,
            "--format", "json",
            "--timeout", "30"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"http-fixture\"");
        result.StandardOutput.ShouldContain("\"Echo\"");
    }
}
