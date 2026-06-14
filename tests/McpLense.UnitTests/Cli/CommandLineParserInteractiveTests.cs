using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Cli;

public class CommandLineParserInteractiveTests
{
    [Fact]
    public void Interactive_LongFlag_SetsInteractive()
    {
        var parsed = CommandLineParser.Parse(["call", "Echo", "https://x/mcp", "--interactive"]);

        parsed.Command.ShouldBe(AppCommand.Call);
        parsed.Interactive.ShouldBeTrue();
    }

    [Fact]
    public void Interactive_ShortFlag_SetsInteractive()
    {
        var parsed = CommandLineParser.Parse(["read", "docs://articles/{id}", "https://x/mcp", "-i"]);

        parsed.Command.ShouldBe(AppCommand.Read);
        parsed.Interactive.ShouldBeTrue();
    }

    [Fact]
    public void Interactive_OnPrompt_SetsInteractive()
    {
        var parsed = CommandLineParser.Parse(["prompt", "Greet", "https://x/mcp", "--interactive"]);

        parsed.Interactive.ShouldBeTrue();
    }

    [Fact]
    public void Interactive_NotSpecified_DefaultsFalse()
    {
        var parsed = CommandLineParser.Parse(["call", "Echo", "https://x/mcp"]);

        parsed.Interactive.ShouldBeFalse();
    }

    [Fact]
    public void Interactive_OnScan_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse(["scan", "https://x/mcp", "--interactive"]));
        ex.Message.ShouldContain("--interactive is only valid for call, read, and prompt.");
    }

    [Fact]
    public void Interactive_OnInspect_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect", "https://x/mcp", "-i"]));
}
