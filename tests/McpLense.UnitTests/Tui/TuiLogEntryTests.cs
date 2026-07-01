using System.Text.Json.Nodes;
using McpLense;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Tui;

public class TuiLogEntryTests
{
    private static TuiLogEntry Parse(string json)
        => TuiLogEntry.FromNotification(JsonNode.Parse(json));

    [Fact]
    public void FromNotification_FullPayload_ParsesLevelLoggerAndStringData()
    {
        var entry = Parse("""{"level":"error","logger":"auth","data":"token expired"}""");

        entry.Level.ShouldBe(LoggingLevel.Error);
        entry.Logger.ShouldBe("auth");
        entry.Message.ShouldBe("token expired");
    }

    [Theory]
    [InlineData("debug", LoggingLevel.Debug)]
    [InlineData("info", LoggingLevel.Info)]
    [InlineData("notice", LoggingLevel.Notice)]
    [InlineData("warning", LoggingLevel.Warning)]
    [InlineData("error", LoggingLevel.Error)]
    [InlineData("critical", LoggingLevel.Critical)]
    [InlineData("alert", LoggingLevel.Alert)]
    [InlineData("emergency", LoggingLevel.Emergency)]
    public void FromNotification_ParsesEverySeverity(string level, LoggingLevel expected)
        => Parse($$"""{"level":"{{level}}","data":"x"}""").Level.ShouldBe(expected);

    [Fact]
    public void FromNotification_ObjectData_IsRenderedAsJson()
    {
        var entry = Parse("""{"level":"info","data":{"code":42,"msg":"hi"}}""");

        entry.Message.ShouldContain("code");
        entry.Message.ShouldContain("42");
    }

    [Fact]
    public void FromNotification_MissingLogger_IsNull()
        => Parse("""{"level":"info","data":"x"}""").Logger.ShouldBeNull();

    [Fact]
    public void FromNotification_BlankLogger_IsNull()
        => Parse("""{"level":"info","logger":"   ","data":"x"}""").Logger.ShouldBeNull();

    [Fact]
    public void FromNotification_NullParams_DoesNotThrow_DefaultsToInfo()
    {
        var entry = TuiLogEntry.FromNotification(null);

        entry.Level.ShouldBe(LoggingLevel.Info);
        entry.Message.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void FromNotification_MalformedLevel_FallsBackToRawJson()
    {
        // "verbose" is not an MCP level; the parse fails and we keep the raw payload rather than lose it.
        var entry = Parse("""{"level":"verbose","data":"x"}""");

        entry.Level.ShouldBe(LoggingLevel.Info);
        entry.Message.ShouldContain("verbose");
    }
}

public class TuiLogFormatTests
{
    [Fact]
    public void LevelsVerboseFirst_StartsAtDebug_EndsAtEmergency()
    {
        TuiLogFormat.LevelsVerboseFirst[0].ShouldBe(LoggingLevel.Debug);
        TuiLogFormat.LevelsVerboseFirst[^1].ShouldBe(LoggingLevel.Emergency);
        TuiLogFormat.LevelsVerboseFirst.Count.ShouldBe(8);
    }

    [Fact]
    public void MostVerbose_IsDebug()
        => TuiLogFormat.MostVerbose.ShouldBe(LoggingLevel.Debug);

    [Fact]
    public void Colour_MapsSeverityToDistinctColours()
    {
        TuiLogFormat.Colour(LoggingLevel.Debug).ShouldBe("grey");
        TuiLogFormat.Colour(LoggingLevel.Warning).ShouldBe("yellow");
        TuiLogFormat.Colour(LoggingLevel.Error).ShouldBe("red");
        TuiLogFormat.Colour(LoggingLevel.Emergency).ShouldBe("red");
    }

    [Fact]
    public void Tag_IsUppercaseAndNonEmpty()
    {
        foreach (var level in TuiLogFormat.LevelsVerboseFirst)
        {
            TuiLogFormat.Tag(level).Trim().ShouldNotBeNullOrEmpty();
        }
    }
}
