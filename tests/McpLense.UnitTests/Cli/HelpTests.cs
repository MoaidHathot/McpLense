using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Cli;

public class HelpTests
{
    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("help")]
    public void TopLevelHelp_ResolvesToHelpWithNoTopic(string token)
    {
        var parsed = CommandLineParser.Parse([token]);

        parsed.Command.ShouldBe(AppCommand.Help);
        parsed.Subject.ShouldBeNull();
    }

    [Fact]
    public void SubcommandHelpFlag_CarriesTopic()
    {
        var parsed = CommandLineParser.Parse(["scan", "--help"]);

        parsed.Command.ShouldBe(AppCommand.Help);
        parsed.Subject.ShouldBe("Scan");
    }

    [Fact]
    public void HelpWithCommandArg_CarriesTopic()
    {
        var parsed = CommandLineParser.Parse(["help", "read"]);

        parsed.Command.ShouldBe(AppCommand.Help);
        parsed.Subject.ShouldBe("Read");
    }

    [Fact]
    public void CommandHelp_For_KnownTopic_ReturnsFocusedText()
    {
        var text = CommandHelp.For("Scan");

        text.ShouldContain("mcplense scan -");
        text.Length.ShouldBeLessThan(CommandLineHelp.Text.Length);
    }

    [Fact]
    public void CommandHelp_For_NullOrUnknown_ReturnsGlobalReference()
    {
        CommandHelp.For(null).ShouldBe(CommandLineHelp.Text);
        CommandHelp.For("not-a-command").ShouldBe(CommandLineHelp.Text);
    }
}
