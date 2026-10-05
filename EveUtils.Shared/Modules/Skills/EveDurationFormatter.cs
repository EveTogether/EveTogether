using System;
using System.Collections.Generic;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// Formats a training duration the way every SKILLS screen writes it (ET-341 mockup v5): <c>{n}d {n}h</c> from a day
/// up, <c>{n}h {n}m</c> from an hour up, else <c>{n}m</c>. Rounded to the minute; zero or less renders "0m".
/// </summary>
public static class EveDurationFormatter
{
    private const int SecondsPerMinute = 60;
    private const int MinutesPerHour = 60;
    private const int MinutesPerDay = 1440;

    public static string Format(TimeSpan duration)
    {
        var totalMinutes = (long)Math.Round(duration.TotalMinutes);
        if (totalMinutes <= 0)
        {
            return "0m";
        }

        if (totalMinutes >= MinutesPerDay)
        {
            return $"{totalMinutes / MinutesPerDay}d {totalMinutes % MinutesPerDay / MinutesPerHour}h";
        }

        return totalMinutes >= MinutesPerHour
            ? $"{totalMinutes / MinutesPerHour}h {totalMinutes % MinutesPerHour}m"
            : $"{totalMinutes}m";
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
