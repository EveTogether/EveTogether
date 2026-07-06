using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Logging;

/// <summary>
/// Custom <see cref="ILogger"/> that writes Warning and above (Warning/Error/Critical) entries to the
/// <see cref="ILogStore"/>. Warnings are captured so expected-but-noteworthy conditions — e.g. an ESI 404
/// "character is not in a fleet" — stay visible in the in-app log window without sitting in the error category.
/// </summary>
internal sealed class AppLogger(string category, ILogStore store) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var exceptionText = exception?.ToString();
        store.Add(new LogEntry(DateTimeOffset.Now, logLevel, category, message, exceptionText));
    }
}
