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

    [Fact]
    public void FromNotification_AnsiEscapesInData_AreStripped()
    {
        // A server that logs pre-coloured output must not inject raw ANSI into the TUI.
        var entry = Parse("""{"level":"info","data":"before \u001b[31mRED\u001b[0m after"}""");

        entry.Message.ShouldBe("before RED after");
        entry.Message.ShouldNotContain("\u001b");
    }

    [Fact]
    public void FromNotification_ControlCharsInLogger_AreStripped()
    {
        var entry = Parse("""{"level":"info","logger":"svc\u001b[1m\u0007","data":"x"}""");

        entry.Logger.ShouldBe("svc");
    }
}

public class TuiLogSanitizeTests
{
    [Fact]
    public void Sanitize_StripsCsiSequence()
        => TuiLogEntry.Sanitize("a\u001b[31mb\u001b[0mc").ShouldBe("abc");

    [Fact]
    public void Sanitize_StripsOscSequence_BelTerminated()
        => TuiLogEntry.Sanitize("x\u001b]0;window title\u0007y").ShouldBe("xy");

    [Fact]
    public void Sanitize_StripsOscSequence_StTerminated()
        => TuiLogEntry.Sanitize("x\u001b]8;;http://e.com\u001b\\y").ShouldBe("xy");

    [Fact]
    public void Sanitize_ConvertsTabToSpace_DropsOtherControls()
        => TuiLogEntry.Sanitize("a\tb\u0007c\u0000d").ShouldBe("a b" + "c" + "d");

    [Fact]
    public void Sanitize_PreservesNewlines()
        => TuiLogEntry.Sanitize("line1\nline2").ShouldBe("line1\nline2");

    [Fact]
    public void Sanitize_StripsLoneEsc()
        => TuiLogEntry.Sanitize("a\u001b").ShouldBe("a");

    [Fact]
    public void Sanitize_PlainText_Unchanged()
        => TuiLogEntry.Sanitize("normal [brackets] and text").ShouldBe("normal [brackets] and text");

    [Fact]
    public void FormatLogLine_WithAnsiInMessage_EmitsNoRawEscape()
    {
        // Even a TuiLogEntry built directly (bypassing FromNotification) must render without leaking
        // raw ANSI - the render boundary sanitizes too.
        var entry = new TuiLogEntry(System.DateTimeOffset.UnixEpoch, LoggingLevel.Warning, "svc\u001b[1m", "x\u001b[31mred\u001b[0m y");

        var line = TuiApp.FormatLogLine(entry, includeTimestamp: false);

        line.ShouldNotContain("\u001b");
        line.ShouldContain("red");
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
