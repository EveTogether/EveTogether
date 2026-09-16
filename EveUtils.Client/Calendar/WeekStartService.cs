using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Calendar;

/// <summary>
/// The day a week starts on for every week-based view (the runs-overview strip, ET-292; the WEEK summary, ET-294).
/// Persists via the Settings module (key <see cref="SettingKey"/>); default = the system culture's
/// <see cref="DateTimeFormatInfo.FirstDayOfWeek"/> (Monday on nl-NL). Follows the same live-swap pattern as
/// <see cref="Theming.ThemeService"/>: a UI-thread <see cref="Changed"/> event lets open views re-lay out without a
/// restart.
/// </summary>
public interface IWeekStartService
{
    /// <summary>The day the week currently starts on.</summary>
    DayOfWeek FirstDay { get; }

    /// <summary>Applies a week start live and persists the choice (or removes the override if it matches the
    /// system default, so a later culture change is still followed).</summary>
    void Apply(DayOfWeek firstDay);

    /// <summary>Loads the persisted choice (if any) and applies it. Call once at startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised on the UI thread whenever the applied week start changes.</summary>
    event Action<DayOfWeek>? Changed;
}

public sealed class WeekStartService(ISettingRepository settings) : IWeekStartService
{
    public const string SettingKey = "ui.week-start";

    public DayOfWeek FirstDay { get; private set; } = SystemDefault();

    public event Action<DayOfWeek>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var saved = (await settings.ListAsync(cancellationToken))
            .FirstOrDefault(s => s.Key == SettingKey)?.Value;
        if (TryParse(saved, out var firstDay) && firstDay != FirstDay)
            ApplyCore(firstDay);
    }

    public void Apply(DayOfWeek firstDay)
    {
        if (firstDay == FirstDay) return;
        ApplyCore(firstDay);

        _ = firstDay == SystemDefault()
            ? settings.DeleteAsync(SettingKey)
            : settings.UpsertAsync(SettingKey, ToValue(firstDay));
    }

    private void ApplyCore(DayOfWeek firstDay)
    {
        FirstDay = firstDay;
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke(firstDay);
        else Dispatcher.UIThread.Post(() => Changed?.Invoke(firstDay));
    }

    /// <summary>Sunday if the culture's own week starts on Sunday; Monday otherwise (only two choices exist —
    /// a Saturday-first culture such as ar-SA falls back to Monday).</summary>
    public static DayOfWeek SystemDefault() =>
        CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek == DayOfWeek.Sunday
            ? DayOfWeek.Sunday
            : DayOfWeek.Monday;

    public static bool TryParse(string? value, out DayOfWeek firstDay)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "sunday": firstDay = DayOfWeek.Sunday; return true;
            case "monday": firstDay = DayOfWeek.Monday; return true;
            default: firstDay = SystemDefault(); return false;
        }
    }

    private static string ToValue(DayOfWeek firstDay) =>
        firstDay == DayOfWeek.Sunday ? "sunday" : "monday";
}
