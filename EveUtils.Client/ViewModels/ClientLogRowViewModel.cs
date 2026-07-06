using Avalonia.Media;
using EveUtils.Shared.Logging;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One row in the client log window: a captured <see cref="LogEntry"/> formatted for display. The
/// capture filter keeps Warning and above, so the level is surfaced as text tinted by severity (Warning amber,
/// Error/Critical red) and the full exception is offered behind an expander when present.
/// </summary>
public sealed class ClientLogRowViewModel(LogEntry entry)
{
    // Severity tints mirror the theme tokens (ValueBrush amber, RedBrush red, TextDimBrush) so a row reads as a
    // warning vs. an error at a glance.
    private static readonly IBrush WarningBrush = Brush.Parse("#FFF5B042");
    private static readonly IBrush ErrorBrush = Brush.Parse("#FFEF5A5A");
    private static readonly IBrush MutedBrush = Brush.Parse("#FF8A7E6B");

    public string TimeText { get; } = entry.Timestamp.LocalDateTime.ToString("HH:mm:ss");
    public string LevelText { get; } = entry.Level.ToString();
    public IBrush LevelBrush { get; } = entry.Level switch
    {
        LogLevel.Critical or LogLevel.Error => ErrorBrush,
        LogLevel.Warning => WarningBrush,
        _ => MutedBrush
    };
    public string Category { get; } = Shorten(entry.Category);
    public string Message { get; } = entry.Message;
    public string ExceptionText { get; } = entry.ExceptionText ?? "";
    public bool HasException { get; } = !string.IsNullOrEmpty(entry.ExceptionText);

    /// <summary>
    /// The whole entry as plain text for the row's Copy button — full timestamp, level, the untruncated category,
    /// the message and (when present) the exception — so a copied error can be forwarded without hunting through
    /// the window for the surrounding context.
    /// </summary>
    public string CopyText { get; } = Format(entry);

    // Mirror the server Blazor page: tail-truncate a long logger category so the row stays readable.
    private static string Shorten(string category) =>
        category.Length > 40 ? "…" + category[^37..] : category;

    private static string Format(LogEntry entry)
    {
        var header = $"[{entry.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {entry.Level} {entry.Category}";
        return string.IsNullOrEmpty(entry.ExceptionText)
            ? $"{header}\n{entry.Message}"
            : $"{header}\n{entry.Message}\n\n{entry.ExceptionText}";
    }
}
