using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Calendar;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Home;

public enum EarningsPeriodKind
{
    Today,
    Week,
    Month
}

/// <summary>One earnings tile's figures: the period so far, and the previous period up to the same point in it.</summary>
/// <param name="PreviousNet">The previous period's own share up to the same point, or null when that period was not
/// (wholly) tracked — a comparison against a period nothing was recording for would be a fake percentage.</param>
public sealed record EarningsPeriodFigures(
    EarningsPeriodKind Kind,
    DateOnly Start,
    DateOnly PreviousStart,
    DateTime PreviousCutoffLocal,
    int Runs,
    TimeSpan Flown,
    decimal? Net,
    decimal? PerHour,
    IskBreakdown Sources,
    decimal? PreviousNet);

/// <summary>
/// The home's today / this week / this month (ET-324): own-share figures over the runs overview's own activity facts,
/// so a tile and the runs overview's range line add the same activities up the same way. "Today" starts at local
/// midnight; the week starts on the <c>ui.week-start</c> day (ET-297).
/// </summary>
public static class EarningsPeriods
{
    public const int ChartDays = 30;

    /// <summary>The first local day any tile, its comparison or the 30-day chart needs.</summary>
    public static DateOnly ReadFrom(DateOnly today, DayOfWeek firstDay)
    {
        DateOnly previousWeek = WeekMath.StartOf(today, firstDay).AddDays(-7);
        DateOnly previousMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        DateOnly chart = today.AddDays(-(ChartDays - 1));
        return new[] { previousWeek, previousMonth, chart }.Min();
    }

    public static EarningsPeriodFigures For(EarningsPeriodKind kind, IReadOnlyList<RunsActivityFacts> activities,
        DateTime nowLocal, DayOfWeek firstDay, DateOnly? firstTracked)
    {
        DateOnly today = DateOnly.FromDateTime(nowLocal);
        (DateOnly start, DateOnly previousStart) = kind switch
        {
            EarningsPeriodKind.Today => (today, today.AddDays(-1)),
            EarningsPeriodKind.Week => (WeekMath.StartOf(today, firstDay), WeekMath.StartOf(today, firstDay).AddDays(-7)),
            _ => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(-1))
        };

        DateTime startLocal = start.ToDateTime(TimeOnly.MinValue);
        DateTime previousStartLocal = previousStart.ToDateTime(TimeOnly.MinValue);
        // A month is shorter than the one after it now and then: the 31st compares with the previous month's end.
        DateTime previousCutoff = _Earlier(previousStartLocal + (nowLocal - startLocal), startLocal);

        RunsActivityFacts[] current = [.. activities.Where(activity => activity.StartedAtLocal >= startLocal && activity.StartedAtLocal <= nowLocal)];
        RunsActivityFacts[] previous = [.. activities.Where(activity => activity.StartedAtLocal >= previousStartLocal && activity.StartedAtLocal < previousCutoff)];
        bool previousTracked = firstTracked is { } first && first <= previousStart;

        return new EarningsPeriodFigures(
            kind,
            start,
            previousStart,
            previousCutoff,
            current.Length,
            TimeSpan.FromSeconds(current.Sum(activity => activity.Duration.TotalSeconds)),
            RunsActivitySummaryText.Net(current),
            RunsActivitySummaryText.PerHour(current),
            RunsActivitySummaryText.SourcesFor(current),
            previousTracked ? RunsActivitySummaryText.Net(previous) ?? 0m : null);
    }

    /// <summary>Each own character's share today and this month, as the runs summary's BY CHARACTER splits it.</summary>
    public static IReadOnlyDictionary<long, (decimal Today, decimal Month)> ByCharacter(
        IReadOnlyList<RunsActivityFacts> activities, DateTime nowLocal, IReadOnlySet<long> ownCharacterIds)
    {
        DateOnly today = DateOnly.FromDateTime(nowLocal);
        DateOnly monthStart = new(today.Year, today.Month, 1);
        Dictionary<long, (decimal Today, decimal Month)> totals = [];
        foreach (RunsActivityFacts activity in activities.Where(activity => activity.Day >= monthStart && activity.StartedAtLocal <= nowLocal))
            foreach (long characterId in ownCharacterIds)
            {
                if (activity.ShareOf(characterId, ownCharacterIds.Contains) is not { } share)
                    continue;

                (decimal dayTotal, decimal monthTotal) = totals.GetValueOrDefault(characterId);
                totals[characterId] = (activity.Day == today ? dayTotal + share : dayTotal, monthTotal + share);
            }

        return totals;
    }

    private static DateTime _Earlier(DateTime left, DateTime right) => left < right ? left : right;
}
