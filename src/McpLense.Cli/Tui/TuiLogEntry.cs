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
        => string.IsNullOrWhiteSpace(logger) ? null : Sanitize(logger).Trim();

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
                JsonValueKind.String => Sanitize(data.GetString() ?? string.Empty),
                JsonValueKind.Null or JsonValueKind.Undefined => "(no data)",
                JsonValueKind.Number => data.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => Truncate(Sanitize(data.GetRawText()))
            };
        }
        catch (Exception)
        {
            return "(unreadable data)";
        }
    }

    /// <summary>
    /// Strips terminal-corrupting sequences from server-supplied log text: ANSI escape sequences
    /// (CSI <c>ESC[…</c>, OSC <c>ESC]…</c>, and other <c>ESC</c>-introduced runs) and C0/C1 control
    /// characters (except tab, which becomes a space). Without this, a server that logs pre-coloured
    /// output would inject raw ANSI that overrides McpLense's own colouring and leaks a reset that
    /// blanks the rest of the line. Newlines are preserved (callers collapse them for single-line
    /// rendering).
    /// </summary>
    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\u001b') // ESC - start of an ANSI escape sequence
            {
                if (i + 1 < text.Length)
                {
                    var next = text[i + 1];
                    if (next == '[') // CSI: ESC [ ... <final byte 0x40-0x7E>
                    {
                        i += 2;
                        while (i < text.Length && !(text[i] >= '\u0040' && text[i] <= '\u007e')) i++;
                        continue; // skip the final byte too (loop's i++)
                    }
                    if (next == ']') // OSC: ESC ] ... BEL or ESC \
                    {
                        i += 2;
                        while (i < text.Length && text[i] != '\u0007'
                               && !(text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '\\')) i++;
                        if (i < text.Length && text[i] == '\u001b') i++; // consume the '\' of ST
                        continue;
                    }
                    // Other ESC-introduced two-char sequences: drop ESC + the following byte.
                    i++;
                    continue;
                }
                continue; // lone trailing ESC
            }

            if (c == '\t')
            {
                sb.Append(' ');
                continue;
            }

            // Drop remaining C0 controls (except \n and \r which callers handle) and DEL/C1.
            if ((c < '\u0020' && c is not '\n' and not '\r') || (c >= '\u007f' && c <= '\u009f'))
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
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

