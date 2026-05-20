using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpLense.Diagnostics;

/// <summary>
/// Static façade for McpLense's user-visible diagnostic stream.
/// </summary>
/// <remarks>
/// <para>
/// Historically the CLI scattered <c>Console.Error.WriteLine</c> calls across the executor,
/// the scan dispatcher, the overlay applicator, and the entry-point. That worked, but it
/// hard-wired the output to <see cref="Console.Error"/>, made the lines hard to silence /
/// redirect from a host process, and couldn't be filtered or routed alongside the
/// <see cref="ILogger"/> pipeline that the scan code already uses.
/// </para>
/// <para>
/// This façade is the single sink for every diagnostic line McpLense writes outside of the
/// structured report payload. By default it writes verbatim to <see cref="Console.Error"/>
/// so existing test assertions on exact stderr lines still pass; embedding hosts can call
/// <see cref="UseLoggerFactory(ILoggerFactory)"/> to redirect the same lines into their
/// own <see cref="ILogger"/> pipeline (Serilog, NLog, OpenTelemetry, etc.) instead.
/// </para>
/// <para>
/// The category is always <c>"McpLense"</c>; the structured log level is always
/// <see cref="LogLevel.Information"/>. The point of the indirection is interception, not
/// per-event filtering - users who want filter granularity should swap in their own
/// logger factory.
/// </para>
/// </remarks>
public static class McpLenseLog
{
    private static ILogger _logger = new StderrLogger();

    /// <summary>
    /// Redirect every <see cref="Write(string)"/> call to the supplied
    /// <see cref="ILoggerFactory"/>. Pass <see cref="NullLoggerFactory.Instance"/> to
    /// silence diagnostics entirely (handy in tests and in `--quiet` host wrappers).
    /// </summary>
    public static void UseLoggerFactory(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _logger = factory.CreateLogger("McpLense");
    }

    /// <summary>Restore the default stderr-writing logger. Used by tests to reset state.</summary>
    public static void ResetToDefault() => _logger = new StderrLogger();

    /// <summary>
    /// Emit one diagnostic line. Format-string overloads aren't provided on purpose - the
    /// callers already build the final string from interpolation and the structured-logging
    /// benefits don't apply to lines that are meant to be human-readable stderr.
    /// </summary>
    public static void Write(string message)
    {
        if (message is null) return;
        _logger.LogInformation("{Message}", message);
    }

    /// <summary>Emit a blank line (separator between help text and error message).</summary>
    public static void WriteBlank() => Write(string.Empty);

    /// <summary>
    /// Built-in logger that writes the raw message to <see cref="Console.Error"/> with no
    /// prefix - the historic format every test in the repo asserts against.
    /// </summary>
    private sealed class StderrLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            // Preserve the no-op blank-line case so callers can rely on WriteBlank() == "\n".
            Console.Error.WriteLine(msg ?? string.Empty);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
