using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Runs;

/// <summary>One calendar day of runs: what <see cref="RunTotals"/> adds up over the activities started in it, and the same
/// per kind.</summary>
public sealed record RunsDayFigures(DateOnly Day, DateTime StartsAtLocal, RunTotalsFigures Totals,
    IReadOnlyList<(RunsKind Kind, RunTotalsFigures Totals)> Kinds);

/// <summary>
/// Run totals per day (ET-485). A day runs from <c>dayStart</c> to the next <c>dayStart</c> in local time, so with 06:00 an
/// evening that goes on past midnight is one day. Every day's figures are <see cref="RunTotals"/> over the activities
/// that started in it — the counting <c>runs/summary</c> and the home do — so the days of a month add up to the month.
/// </summary>
public static class RunsDays
{
    public const int MaxDays = 366;

    /// <summary>The kinds a day is split over; <see cref="RunsKind.All"/> is the day itself.</summary>
    private static readonly RunsKind[] SplitKinds = [.. Enum.GetValues<RunsKind>().Where(kind => kind is not RunsKind.All)];

    public static DateOnly DayOf(DateTime startedAtLocal, TimeOnly dayStart) =>
        DateOnly.FromDateTime(startedAtLocal - dayStart.ToTimeSpan());

    /// <summary>Every day from <paramref name="from"/> to <paramref name="to"/>, also the ones without a run. Activities
    /// that have not started by <paramref name="nowLocal"/> are left out, as in a period summary.</summary>
    public static IReadOnlyList<RunsDayFigures> Of(IReadOnlyCollection<RunsActivityFacts> activities, DateOnly from, DateOnly to,
        TimeOnly dayStart, DateTime nowLocal)
    {
        RunsActivityFacts[] started = RunTotals.StartedBetween(activities, from.ToDateTime(dayStart), nowLocal);
        ILookup<DateOnly, RunsActivityFacts> byDay = started.ToLookup(activity => DayOf(activity.StartedAtLocal, dayStart));
        return [.. Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(offset => from.AddDays(offset))
            .Select(day =>
            {
                RunsActivityFacts[] inDay = [.. byDay[day]];
                return new RunsDayFigures(day, day.ToDateTime(dayStart), RunTotals.Of(inDay),
                    [.. SplitKinds
                        .Select(kind => (kind, totals: RunTotals.Of([.. inDay.Where(activity => kind.Matches(activity.TypeId))])))
                        .Where(split => split.totals.Runs > 0)]);
            })];
    }
}
