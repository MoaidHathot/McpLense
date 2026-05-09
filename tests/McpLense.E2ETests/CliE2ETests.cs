using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace McpLense.E2ETests;

public class CliE2ETests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task NoArgs_PrintsHelpAndReturnsZero()
    {
        var result = await CliRunner.RunAsync([], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("mcplense");
        result.StandardOutput.ShouldContain("inspect");
        result.StandardOutput.ShouldContain("Usage");
    }

    [Fact]
    public async Task Version_PrintsVersionAndReturnsZero()
    {
        var result = await CliRunner.RunAsync(["version"], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UnknownCommand_PrintsHelpAndReturnsOne()
    {
        var result = await CliRunner.RunAsync(["nope"], DefaultTimeout);

        result.ExitCode.ShouldBe(1);
        result.StandardError.ShouldContain("Unknown command 'nope'");
        result.StandardError.ShouldContain("mcplense");
    }

    [Fact]
    public async Task MissingTarget_PrintsHelpfulErrorAndReturnsOne()
    {
        var result = await CliRunner.RunAsync(["inspect"], DefaultTimeout);

        result.ExitCode.ShouldBe(1);
        result.StandardError.ShouldContain("Specify a target");
    }

    [Fact]
    public async Task Help_FlagOnSubcommand_PrintsHelpAndReturnsZero()
    {
        var result = await CliRunner.RunAsync(["inspect", "--help"], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("mcplense");
        result.StandardOutput.ShouldContain("Usage");
    }

    [Fact]
    public async Task Inspect_AgainstStdioTestServer_ReturnsZeroAndJsonReport()
    {
        var args = CliRunner.WithStdioTestServer(
            "inspect",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"servers\":");
        result.StandardOutput.ShouldContain("\"capabilities\":");
        result.StandardOutput.ShouldContain("\"Echo\"");
        result.StandardOutput.ShouldContain("\"Add\"");
    }

    [Fact]
    public async Task Tools_AgainstStdioTestServer_ListsKnownTools()
    {
        var args = CliRunner.WithStdioTestServer(
            "tools",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"Echo\"");
        result.StandardOutput.ShouldContain("\"Add\"");
        result.StandardOutput.ShouldContain("\"Divide\"");
        result.StandardOutput.ShouldContain("\"Boom\"");
    }

    [Fact]
    public async Task Resources_AgainstStdioTestServer_ListsKnownResources()
    {
        var args = CliRunner.WithStdioTestServer(
            "resources",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("config://app/settings");
    }

    [Fact]
    public async Task Prompts_AgainstStdioTestServer_ListsKnownPrompts()
    {
        var args = CliRunner.WithStdioTestServer(
            "prompts",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"Greet\"");
        result.StandardOutput.ShouldContain("\"CodeReview\"");
    }

    [Fact]
    public async Task Call_Echo_AgainstStdioTestServer_ReturnsZeroAndEchoesMessage()
    {
        var args = CliRunner.WithStdioTestServer(
            "call", "Echo",
            "--args", "{\"message\":\"hello-e2e\"}",
            "--progress", "false",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("echo: hello-e2e");
    }

    [Fact]
    public async Task Call_Add_AgainstStdioTestServer_ReturnsZeroAndSum()
    {
        var args = CliRunner.WithStdioTestServer(
            "call", "Add",
            "--args", "{\"a\":2,\"b\":40}",
            "--progress", "false",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("42");
    }

    [Fact]
    public async Task Call_Boom_AgainstStdioTestServer_ReturnsOne()
    {
        var args = CliRunner.WithStdioTestServer(
            "call", "Boom",
            "--args", "{}",
            "--progress", "false",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Read_Resource_AgainstStdioTestServer_ReturnsZeroAndPayload()
    {
        var args = CliRunner.WithStdioTestServer(
            "read", "config://app/settings",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("config://app/settings");
        result.StandardOutput.ShouldContain("dark");
    }

    [Fact]
    public async Task Prompt_Greet_AgainstStdioTestServer_ReturnsZeroAndGreeting()
    {
        var args = CliRunner.WithStdioTestServer(
            "prompt", "Greet",
            "--args", "{\"name\":\"world\"}",
            "--format", "json",
            "--timeout", "60");

        var result = await CliRunner.RunAsync(args, DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("Hello, world!");
    }

    [Fact]
    public async Task Inspect_WithConfigFile_ResolvesAndReturnsZero()
    {
        using var dir = new TempDirectory();
        var configPath = dir.WriteFile("mcp.json", $$"""
        {
          "mcpServers": {
            "fixture": {
              "command": "dotnet",
              "args": ["exec", "{{BuildArtifacts.TestServerDll.Replace("\\", "\\\\")}}"]
            }
          }
        }
        """);

        var result = await CliRunner.RunAsync([
            "inspect",
            "--config", configPath,
            "--format", "json",
            "--timeout", "60"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"fixture\"");
        result.StandardOutput.ShouldContain("\"Echo\"");
    }

    [Fact]
    public async Task Inspect_WithConfigFile_AndServerFilter_ReturnsZero()
    {
        using var dir = new TempDirectory();
        var configPath = dir.WriteFile("mcp.json", $$"""
        {
          "mcpServers": {
            "fixture": {
              "command": "dotnet",
              "args": ["exec", "{{BuildArtifacts.TestServerDll.Replace("\\", "\\\\")}}"]
            },
            "ignored": {
              "command": "dotnet",
              "args": ["exec", "does-not-exist.dll"]
            }
          }
        }
        """);

        var result = await CliRunner.RunAsync([
            "inspect",
            "--config", configPath,
            "--server", "fixture",
            "--format", "json",
            "--timeout", "60"
        ], DefaultTimeout);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("\"fixture\"");
        result.StandardOutput.ShouldNotContain("\"ignored\"");
    }
}
