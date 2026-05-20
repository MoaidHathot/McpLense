using McpLense.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Diagnostics;

/// <summary>
/// Tests the <see cref="McpLenseLog"/> façade. The default sink must continue to write
/// verbatim to <see cref="Console.Error"/> (so existing CLI test assertions on exact
/// stderr lines keep passing), and <see cref="McpLenseLog.UseLoggerFactory"/> must
/// redirect every subsequent call into the supplied factory.
/// </summary>
public class McpLenseLogTests : IDisposable
{
    public void Dispose() => McpLenseLog.ResetToDefault();

    [Fact]
    public void DefaultSink_WritesToStderr_Verbatim()
    {
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            McpLenseLog.Write("hello world");
            McpLenseLog.WriteBlank();
            McpLenseLog.Write("second line");
        }
        finally
        {
            Console.SetError(original);
        }

        var lines = captured.ToString().Split(Environment.NewLine);
        lines.ShouldContain("hello world");
        lines.ShouldContain("second line");
        // The blank line is preserved as an empty entry between the two real ones.
        Array.IndexOf(lines, "second line").ShouldBeGreaterThan(Array.IndexOf(lines, "hello world"));
    }

    [Fact]
    public void UseLoggerFactory_RedirectsAllSubsequentWrites()
    {
        var sink = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(sink));
        McpLenseLog.UseLoggerFactory(factory);

        McpLenseLog.Write("alpha");
        McpLenseLog.Write("beta");

        sink.Messages.ShouldBe(new[] { "alpha", "beta" });
    }

    [Fact]
    public void UseLoggerFactory_UsesMcpLenseCategory()
    {
        var sink = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(sink));
        McpLenseLog.UseLoggerFactory(factory);

        McpLenseLog.Write("hi");

        sink.Categories.ShouldContain("McpLense");
    }

    [Fact]
    public void NullLoggerFactory_SilencesEverything()
    {
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            McpLenseLog.UseLoggerFactory(NullLoggerFactory.Instance);
            McpLenseLog.Write("should not appear on stderr");
        }
        finally
        {
            Console.SetError(original);
        }

        captured.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void ResetToDefault_RestoresStderrBehaviour()
    {
        // Redirect first, then reset, then verify stderr writes resume.
        var sink = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(sink));
        McpLenseLog.UseLoggerFactory(factory);
        McpLenseLog.ResetToDefault();

        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            McpLenseLog.Write("after reset");
        }
        finally
        {
            Console.SetError(original);
        }

        captured.ToString().ShouldContain("after reset");
        sink.Messages.ShouldBeEmpty();
    }

    [Fact]
    public void UseLoggerFactory_NullArgument_Throws()
        => Should.Throw<ArgumentNullException>(() => McpLenseLog.UseLoggerFactory(null!));

    // -- Helpers --------------------------------------------------------

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();
        public List<string> Categories { get; } = new();
        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return new ListLogger(Messages);
        }
        public void Dispose() { }
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _sink;
        public ListLogger(List<string> sink) => _sink = sink;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _sink.Add(formatter(state, exception));
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
