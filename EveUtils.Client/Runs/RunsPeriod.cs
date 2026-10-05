using System;
using EveUtils.Client.Calendar;

namespace EveUtils.Client.Runs;

/// <summary>A span the run totals are counted over. "Today" starts at local midnight and the week on the
/// <c>ui.week-start</c> day (ET-297), as on the home's earnings tiles; a session starts with the app (ET-436).</summary>
public enum RunsPeriod
{
    Session,
    Today,
    Week,
    Month
}

public static class RunsPeriods
{
    /// <summary>The local day a calendar period starts on.</summary>
    public static DateOnly FirstDayOf(RunsPeriod period, DateOnly today, DayOfWeek firstDay) => period switch
    {
        RunsPeriod.Today => today,
        RunsPeriod.Week => WeekMath.StartOf(today, firstDay),
        RunsPeriod.Month => new DateOnly(today.Year, today.Month, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "A session has no calendar day to start on.")
    };

    public static DateTime StartOf(RunsPeriod period, DateTime nowLocal, DayOfWeek firstDay, DateTime sessionStartLocal) =>
        period is RunsPeriod.Session
            ? sessionStartLocal
            : FirstDayOf(period, DateOnly.FromDateTime(nowLocal), firstDay).ToDateTime(TimeOnly.MinValue);
}
