using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.UiTests;

/// <summary>Keeps the formatted messages so a test can read what a code path said, not only that it ran.</summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => [.. _messages];

    public ILogger CreateLogger(string categoryName) => new Recorder(_messages);

    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}
