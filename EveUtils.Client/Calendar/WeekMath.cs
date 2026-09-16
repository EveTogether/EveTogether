using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveUtils.Client.Calendar;

/// <summary>
/// The one week calculation the runs-overview strip (ET-292) and the WEEK summary (ET-294) both call, so a
/// Monday-start and a Sunday-start view of the same data always agree on where a week begins. Local dates only —
/// no times, and never <c>+7×24h</c> arithmetic, which breaks across a DST change (<see cref="StartOf"/> is exact
/// there because <see cref="DateOnly"/> has no time-of-day to shift).
/// </summary>
public static class WeekMath
{
    /// <summary>The first day of the week containing <paramref name="day"/>, for the given week start.</summary>
    public static DateOnly StartOf(DateOnly day, DayOfWeek firstDay) =>
        day.AddDays(-((7 + (day.DayOfWeek - firstDay)) % 7));

    /// <summary>The seven days of a week, in display order, starting at <paramref name="firstDay"/>.</summary>
    public static IReadOnlyList<DayOfWeek> DaysInOrder(DayOfWeek firstDay) =>
        Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)firstDay + i) % 7)).ToArray();

    /// <summary>
    /// A week's range as a label, e.g. <c>7–13 SEP</c>, or <c>28 SEP – 4 OCT</c> where the week crosses a month
    /// boundary. Never an ISO week number — ISO weeks are always Monday-based, so one would name a different range
    /// than the strip shows under a Sunday start (see the type doc).
    /// </summary>
    public static string RangeText(DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(6);
        var startMonth = MonthAbbreviation(weekStart);
        var endMonth = MonthAbbreviation(weekEnd);

        return startMonth == endMonth
            ? $"{weekStart.Day}–{weekEnd.Day} {endMonth}"
            : $"{weekStart.Day} {startMonth} – {weekEnd.Day} {endMonth}";
    }

    private static string MonthAbbreviation(DateOnly date) =>
        date.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
}
