using System.IO;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Cli;

/// <summary>
/// Locks in CLI parsing for the consumer-requested fleet-scale flags:
/// <c>--targets-from</c>, <c>--http-only</c>, <c>--default-scope</c>, <c>--format jsonl</c>.
/// </summary>
public class CommandLineParserFleetFlagsTests
{
    [Fact]
    public void Parse_ScanCommand_TargetsFrom_PopulatesPaths()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "https://a/mcp\n");
            var parsed = CommandLineParser.Parse(["scan", "--targets-from", tmp]);

            parsed.Command.ShouldBe(AppCommand.Scan);
            parsed.TargetsFromPaths.ShouldNotBeNull();
            parsed.TargetsFromPaths!.Count.ShouldBe(1);
            parsed.TargetsFromPaths![0].ShouldBe(tmp);
            // --targets-from satisfies the "you must specify a target" requirement.
            parsed.Target.Url.ShouldBeNull();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Parse_TargetsFrom_OnNonScan_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "https://a/mcp", "--targets-from", "x.txt"
        ])).Message.ShouldContain("targets-from");
    }

    [Fact]
    public void Parse_HttpOnly_OnScan_SetsFlag()
    {
        var parsed = CommandLineParser.Parse(["scan", "https://a/mcp", "--http-only"]);
        parsed.HttpOnly.ShouldBeTrue();
        parsed.Target.HttpOnly.ShouldBeTrue();
    }

    [Fact]
    public void Parse_HttpOnly_OnNonScan_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "https://a/mcp", "--http-only"
        ])).Message.ShouldContain("http-only");
    }

    [Fact]
    public void Parse_DefaultScope_OnScan_PopulatesAuthOverrides()
    {
        var parsed = CommandLineParser.Parse([
            "scan", "https://a/mcp", "--default-scope", "api://my-aad/.default"
        ]);
        parsed.DefaultScope.ShouldBe("api://my-aad/.default");
        parsed.Target.AuthOverrides.DefaultScope.ShouldBe("api://my-aad/.default");
    }

    [Fact]
    public void Parse_FormatJsonl_IsAccepted()
    {
        var parsed = CommandLineParser.Parse([
            "scan", "https://a/mcp", "--format", "jsonl"
        ]);
        parsed.Format.ShouldBe(OutputFormat.Jsonl);
    }

    [Fact]
    public void Parse_FormatNdjson_IsAliasForJsonl()
    {
        var parsed = CommandLineParser.Parse([
            "scan", "https://a/mcp", "--format", "ndjson"
        ]);
        parsed.Format.ShouldBe(OutputFormat.Jsonl);
    }
}
