using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiRunner.Tests.Fakes;

/// <summary>One log line a test can look at.</summary>
public sealed record LogLine(LogLevel Level, string Category, string Message);

/// <summary>Keeps every log line in memory, so a test can check what the runner told its owner.</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public IReadOnlyList<LogLine> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, _lines);

    public void Dispose()
    {
        // Nothing to release: the lines stay readable after the host is gone.
    }

    private sealed class ListLogger(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lines.Enqueue(new LogLine(logLevel, category, message));
        }
    }
}
