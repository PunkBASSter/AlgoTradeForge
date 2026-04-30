using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Tests.TestHelpers;

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that records every call into an in-memory list.
/// Use in tests that need to assert on log messages (e.g. WARN-on-deletion contracts).
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    public IEnumerable<LogEntry> Warnings => Entries.Where(e => e.Level == LogLevel.Warning);
}

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
