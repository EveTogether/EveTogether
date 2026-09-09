using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Logging;

/// <summary>
/// Last-chance net for the gap ET-196 found: nothing but Dispatcher.UIThread.UnhandledException was wired up, so
/// anything that escapes on another thread — a bare Thread, an unobserved Task, a UI-thread fault Avalonia's own
/// dispatcher doesn't see — ended the process without a trace anywhere (ET-197). Writes straight to
/// app-errors.jsonl with File.AppendAllText, not through ILogger/ILogStore: by the time this runs, that chain may
/// be exactly what's broken. Never write clipboard or player data here (ET-57) — only the exception and its
/// stack trace, which is also what stands in for "what the app was doing".
/// </summary>
public static class CrashLog
{
    private static string? _filePath;

    public static void Install(string dataDirectory)
    {
        _filePath = Path.Combine(dataDirectory, "app-errors.jsonl");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved(); // already on record; don't let it re-surface via the finalizer thread
        };
    }

    /// <summary>
    /// Call once, as the last line on a clean exit path. Its absence after a session is itself the signal (ET-197
    /// acceptance #4): some crashes — StackOverflowException chief among them — can never be caught, so a missing
    /// shutdown line is the only trace they leave.
    /// </summary>
    public static void WriteShutdownMarker(string context) =>
        AppendLine(new LogEntry(DateTimeOffset.Now, LogLevel.Information, "Crash", $"Clean shutdown: {context}", null));

    private static void Write(string source, Exception? exception)
    {
        var message = exception is null
            ? $"Unhandled non-Exception object thrown via {source}"
            : $"Unhandled exception via {source}";
        AppendLine(new LogEntry(DateTimeOffset.Now, LogLevel.Critical, "Crash", message, exception?.ToString()));
    }

    private static void AppendLine(LogEntry entry)
    {
        if (_filePath is null) return;
        try
        {
            File.AppendAllText(_filePath, System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch { /* last-chance logging must never itself throw */ }
    }
}
