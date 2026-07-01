using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace McpLense;

/// <summary>
/// One structured MCP log entry (from a <c>notifications/message</c>), parsed from the server's
/// <see cref="LoggingMessageNotificationParams"/>. Kept UI-agnostic so both the persistent tail and
/// the full log viewer render from the same shape.
/// </summary>
internal sealed record TuiLogEntry(DateTimeOffset Timestamp, LoggingLevel Level, string? Logger, string Message)
{
    /// <summary>
    /// Best-effort parse of a raw <c>notifications/message</c> params node into a structured entry.
    /// Falls back to <see cref="LoggingLevel.Info"/> and a raw-JSON message when the payload is
    /// malformed, so a noisy or non-conforming server never loses a line.
    /// </summary>
    public static TuiLogEntry FromNotification(JsonNode? parameters)
    {
        var now = DateTimeOffset.Now;
        if (parameters is null)
        {
            return new TuiLogEntry(now, LoggingLevel.Info, null, "(empty log notification)");
        }

        try
        {
            var parsed = parameters.Deserialize<LoggingMessageNotificationParams>(McpJson.Options);
            if (parsed is not null)
            {
                return new TuiLogEntry(now, parsed.Level, NormalizeLogger(parsed.Logger), RenderData(parsed.Data));
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw rendering below.
        }

        return new TuiLogEntry(now, LoggingLevel.Info, null, Truncate(parameters.ToJsonString()));
    }

    private static string? NormalizeLogger(string? logger)
        => string.IsNullOrWhiteSpace(logger) ? null : logger.Trim();

    /// <summary>
    /// Renders the free-form <c>data</c> field for a single line: a JSON string is shown verbatim,
    /// everything else (object/array/number) is compacted to JSON so structure survives.
    /// </summary>
    private static string RenderData(JsonElement data)
    {
        try
        {
            return data.ValueKind switch
            {
                JsonValueKind.String => data.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => "(no data)",
                JsonValueKind.Number => data.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => Truncate(data.GetRawText())
            };
        }
        catch (Exception)
        {
            return "(unreadable data)";
        }
    }

    private static string Truncate(string text, int max = 2000)
        => text.Length <= max ? text : text[..(max - 1)] + "\u2026";
}

/// <summary>Shared JSON options for deserialising SDK notification payloads.</summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// Presentation helpers for <see cref="LoggingLevel"/>: the ordered level list (most-&gt;least
/// verbose), short uppercase labels, and a Spectre colour per severity so the log views read like a
/// real console log.
/// </summary>
internal static class TuiLogFormat
{
    /// <summary>The 8 MCP/syslog levels, most verbose first (Debug) to most severe last (Emergency).</summary>
    public static readonly IReadOnlyList<LoggingLevel> LevelsVerboseFirst =
    [
        LoggingLevel.Debug,
        LoggingLevel.Info,
        LoggingLevel.Notice,
        LoggingLevel.Warning,
        LoggingLevel.Error,
        LoggingLevel.Critical,
        LoggingLevel.Alert,
        LoggingLevel.Emergency
    ];

    /// <summary>The most verbose level - used as the default so every log surfaces until narrowed.</summary>
    public const LoggingLevel MostVerbose = LoggingLevel.Debug;

    /// <summary>Fixed-width uppercase tag, e.g. <c>WARN</c>, for aligned log columns.</summary>
    public static string Tag(LoggingLevel level) => level switch
    {
        LoggingLevel.Debug => "DEBUG",
        LoggingLevel.Info => "INFO ",
        LoggingLevel.Notice => "NOTE ",
        LoggingLevel.Warning => "WARN ",
        LoggingLevel.Error => "ERROR",
        LoggingLevel.Critical => "CRIT ",
        LoggingLevel.Alert => "ALERT",
        LoggingLevel.Emergency => "EMERG",
        _ => level.ToString().ToUpperInvariant()
    };

    /// <summary>Spectre colour name for the level (used for both the tag and the message tint).</summary>
    public static string Colour(LoggingLevel level) => level switch
    {
        LoggingLevel.Debug => "grey",
        LoggingLevel.Info => "blue",
        LoggingLevel.Notice => "aqua",
        LoggingLevel.Warning => "yellow",
        LoggingLevel.Error => "red",
        LoggingLevel.Critical => "red",
        LoggingLevel.Alert => "red",
        LoggingLevel.Emergency => "red",
        _ => "white"
    };

    /// <summary>Human label for the level (title-case), used in the level picker.</summary>
    public static string Name(LoggingLevel level) => level.ToString();
}

