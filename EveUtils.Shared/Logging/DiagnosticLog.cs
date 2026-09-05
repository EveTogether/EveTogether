using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Logging;

/// <summary>
/// A log line that is worth keeping in app-errors.jsonl without claiming something went wrong.
/// AppLogger drops Information everywhere except lines carrying <see cref="Marker"/>.
/// </summary>
public static class DiagnosticLog
{
    public static readonly EventId Marker = new(900100, "Diagnostic");

    public static void LogDiagnostic(this ILogger logger, string message, params object?[] args) =>
        logger.LogInformation(Marker, message, args);
}
