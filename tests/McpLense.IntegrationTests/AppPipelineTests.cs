using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests;

[Collection("McpExecutor")]
public class AppPipelineTests
{
    [Fact]
    public async Task RunAsync_NoArgs_PrintsHelpAndReturnsZero()
    {
        using var capture = new ConsoleCapture();

        var exitCode = await App.RunAsync([]);

        exitCode.ShouldBe(0);
        capture.StandardOutput.ShouldContain("mcplense");
        capture.StandardOutput.ShouldContain("inspect");
    }

    [Fact]
    public async Task RunAsync_VersionCommand_PrintsVersionAndReturnsZero()
    {
        using var capture = new ConsoleCapture();

        var exitCode = await App.RunAsync(["version"]);

        exitCode.ShouldBe(0);
        capture.StandardOutput.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunAsync_UnknownCommand_PrintsHelpAndReturnsOne()
    {
        using var capture = new ConsoleCapture();

        var exitCode = await App.RunAsync(["nope"]);

        exitCode.ShouldBe(1);
        capture.StandardError.ShouldContain("Unknown command 'nope'");
        capture.StandardError.ShouldContain("mcplense");
    }

    [Fact]
    public async Task RunAsync_MissingTarget_PrintsHelpfulError()
    {
        using var capture = new ConsoleCapture();

        var exitCode = await App.RunAsync(["inspect"]);

        exitCode.ShouldBe(1);
        capture.StandardError.ShouldContain("Specify a target");
    }

    [Fact]
    public async Task RunAsync_InspectAgainstTestServer_ReturnsZeroAndJson()
    {
        using var capture = new ConsoleCapture();

        var exitCode = await App.RunAsync([
            "inspect",
            "--format", "json",
            "--timeout", "60",
            "--",
            "dotnet", "exec", TestServerLocator.TestServerDll
        ]);

        exitCode.ShouldBe(0);
        capture.StandardOutput.ShouldContain("\"servers\":");
        capture.StandardOutput.ShouldContain("\"capabilities\":");
        capture.StandardOutput.ShouldContain("\"Echo\"");
    }

    [Fact]
    public async Task RunAsync_ToolsAgainstTestServer_ReturnsZeroAndContainsTools()
    {
        using var capture = new ConsoleCapture();

        var exitCode = await App.RunAsync([
            "tools",
            "--format", "json",
            "--timeout", "60",
            "--",
            "dotnet", "exec", TestServerLocator.TestServerDll
        ]);

        exitCode.ShouldBe(0);
        capture.StandardOutput.ShouldContain("\"Echo\"");
        capture.StandardOutput.ShouldContain("\"Add\"");
    }

    [Fact]
    public async Task RunAsync_CallEchoAgainstTestServer_ReturnsZero()
    {
        using var capture = new ConsoleCapture();

        var exitCode = await App.RunAsync([
            "call", "Echo",
            "--args", "{\"message\":\"hi\"}",
            "--progress", "false",
            "--format", "json",
            "--timeout", "60",
            "--",
            "dotnet", "exec", TestServerLocator.TestServerDll
        ]);

        exitCode.ShouldBe(0);
        capture.StandardOutput.ShouldContain("echo: hi");
    }

    [Fact]
    public async Task RunAsync_ConfigPath_ResolvesAndInspects()
    {
        using var dir = new TempDirectory();
        var configPath = dir.WriteFile("mcp.json", $$"""
        {
          "mcpServers": {
            "fixture": {
              "command": "dotnet",
              "args": ["exec", "{{TestServerLocator.TestServerDll.Replace("\\", "\\\\")}}"]
            }
          }
        }
        """);

        using var capture = new ConsoleCapture();

        var exitCode = await App.RunAsync([
            "inspect",
            "--config", configPath,
            "--format", "json",
            "--timeout", "60"
        ]);

        exitCode.ShouldBe(0);
        capture.StandardOutput.ShouldContain("\"fixture\"");
        capture.StandardOutput.ShouldContain("\"Echo\"");
    }
}

internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;

    public ConsoleCapture()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        _outWriter = new StringWriter();
        _errWriter = new StringWriter();
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public string StandardOutput => _outWriter.ToString();

    public string StandardError => _errWriter.ToString();

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _outWriter.Dispose();
        _errWriter.Dispose();
    }
}
