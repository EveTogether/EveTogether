using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Logging;

/// <summary>An immutable in-app log entry captured by the custom log provider.</summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? ExceptionText);
