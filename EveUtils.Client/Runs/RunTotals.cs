using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.Runs;

/// <summary>What a set of activities adds up to: how many, how long they were flown, the own share and its rate.</summary>
public sealed record RunTotalsFigures(int Runs, TimeSpan Flown, decimal? Net, decimal? PerHour, IskBreakdown Sources);

/// <summary>
/// The one place run totals and ISK/hour are added up (ET-436): the runs overview's day headers, strip and summary, the
/// home's earnings tiles and the Local API's <c>runs/summary</c> all count through here, so a figure on screen and the
/// same figure on a stream overlay cannot disagree.
/// </summary>
public static class RunTotals
{
    public static RunTotalsFigures Of<T>(IReadOnlyCollection<T> activities) where T : IRunsActivityFigures =>
        new(activities.Count, Flown(activities), Net(activities), PerHour(activities), Sources(activities));

    /// <summary>The activities a period holds: started at or after its start, and not after now.</summary>
    public static T[] StartedBetween<T>(IEnumerable<T> activities, DateTime fromLocal, DateTime nowLocal)
        where T : IRunsActivityFigures =>
        [.. activities.Where(activity => activity.StartedAtLocal >= fromLocal && activity.StartedAtLocal <= nowLocal)];

    public static TimeSpan Flown<T>(IEnumerable<T> activities) where T : IRunsActivityFigures =>
        TimeSpan.FromSeconds(activities.Sum(activity => activity.Duration.TotalSeconds));

    /// <summary>The own share over these activities, or null where none of them was valued — never a "0" that would
    /// read as a valuation taken and come out at nothing (ET-161 AC-4).</summary>
    public static decimal? Net<T>(IEnumerable<T> activities) where T : IRunsActivityFigures
    {
        decimal[] known = [.. activities.Select(activity => activity.NetIsk).OfType<decimal>()];
        return known.Length == 0 ? null : known.Sum();
    }

    /// <summary>The own share per flown hour, or null with nothing timed and valued. Only over what has a flown time:
    /// an activity with a run left without a stop reads 0 flown, and counting its ISK over nobody's hours would
    /// inflate the rate.</summary>
    public static decimal? PerHour<T>(IEnumerable<T> activities) where T : IRunsActivityFigures
    {
        T[] timed = [.. activities.Where(activity => activity.Duration > TimeSpan.Zero)];
        double hours = timed.Sum(activity => activity.Duration.TotalHours);
        return hours > 0 && Net(timed) is { } net ? net / (decimal)hours : null;
    }

    /// <summary>What each source brought in over the same activities (ET-290) — the very breakdowns <see cref="Net"/>
    /// adds up, split by source rather than summed a second way.</summary>
    public static IskBreakdown Sources<T>(IEnumerable<T> activities) where T : IRunsActivityFigures =>
        new([.. activities
            .SelectMany(activity => activity.Isk.Contributions)
            .Where(part => part.Certainty is not IskCertainty.Unknown)
            .GroupBy(part => part.Source)
            .Select(source => new IskContribution(source.Key, source.Sum(part => part.Amount), source.First().Certainty))]);
}
