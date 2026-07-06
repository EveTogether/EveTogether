using System;
using System.Collections.Generic;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// Formats a training duration the EVE way: <c>{n}mo {n}d {n}h {n}m</c> using 30-day months, dropping any
/// leading zero unit and only showing units down to the minute. A sub-minute or zero duration renders "0m".
/// </summary>
public static class EveDurationFormatter
{
    private const int SecondsPerMinute = 60;
    private const int MinutesPerHour = 60;
    private const int HoursPerDay = 24;
    private const int DaysPerMonth = 30; // EVE convention

    public static string Format(TimeSpan duration)
    {
        var totalMinutes = (long)Math.Floor(duration.TotalMinutes);
        if (totalMinutes <= 0)
            return "0m";

        var minutes = totalMinutes % MinutesPerHour;
        var totalHours = totalMinutes / MinutesPerHour;
        var hours = totalHours % HoursPerDay;
        var totalDays = totalHours / HoursPerDay;
        var days = totalDays % DaysPerMonth;
        var months = totalDays / DaysPerMonth;

        var parts = new List<string>(4);
        if (months > 0) parts.Add($"{months}mo");
        if (days > 0 || parts.Count > 0) parts.Add($"{days}d");
        if (hours > 0 || parts.Count > 0) parts.Add($"{hours}h");
        parts.Add($"{minutes}m");
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Formats a short duration down to the second — <c>{n}h {n}m {n}s</c>, dropping any leading zero unit (e.g.
    /// "8m 29s", "45s", "1h 3m 5s"). For capacitor-style timescales where the seconds matter; "0s" for a zero duration.
    /// </summary>
    public static string FormatWithSeconds(TimeSpan duration)
    {
        var totalSeconds = (long)Math.Floor(duration.TotalSeconds);
        if (totalSeconds <= 0)
            return "0s";

        var seconds = totalSeconds % SecondsPerMinute;
        var totalMinutes = totalSeconds / SecondsPerMinute;
        var minutes = totalMinutes % MinutesPerHour;
        var hours = totalMinutes / MinutesPerHour;

        var parts = new List<string>(3);
        if (hours > 0) parts.Add($"{hours}h");
        if (minutes > 0 || parts.Count > 0) parts.Add($"{minutes}m");
        parts.Add($"{seconds}s");
        return string.Join(' ', parts);
    }
}
