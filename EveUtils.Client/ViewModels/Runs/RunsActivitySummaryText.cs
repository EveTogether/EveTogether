using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>What an activity contributes to a total: when it started, how long it was flown and what this machine's
/// own characters made of it. A row on screen is one, and so is an activity the strip (ET-292) counts without ever
/// building a row for it — which is how a day header, a strip cell and the range line add up the very same figures.</summary>
public interface IRunsActivityFigures
{
    DateTime StartedAtLocal { get; }

    TimeSpan Duration { get; }

    /// <summary>The own share's total, or null where nothing of it was valued.</summary>
    decimal? NetIsk { get; }

    IskBreakdown Isk { get; }
}

/// <summary>The "N activities", flown time and net-ISK phrases a day header, a strip cell and the range line (ET-233,
/// ET-292) all say about a set of activities — one formula so none of them can disagree over activities they share.
/// Never a "0 ISK": a zero here would read as a valuation that was taken and came out at nothing, rather than nothing
/// having been valued at all (ET-161 AC-4).</summary>
internal static class RunsActivitySummaryText
{
    public static string ActivitiesCount(int count) => $"{count} {(count == 1 ? "activity" : "activities")}";

    public static string FlownFor<T>(IReadOnlyList<T> activities) where T : IRunsActivityFigures =>
        Flown(activities) + " flown";

    /// <summary>"24:18:33" — hours past a day keep counting rather than wrapping.</summary>
    public static string Flown<T>(IReadOnlyCollection<T> activities) where T : IRunsActivityFigures
    {
        var flown = TimeSpan.FromSeconds(activities.Sum(activity => activity.Duration.TotalSeconds));
        return $"{(int)flown.TotalHours}:{flown.Minutes:00}:{flown.Seconds:00}";
    }

    public static string NetFor<T>(IReadOnlyList<T> activities) where T : IRunsActivityFigures =>
        Net(activities) is { } net ? Signed(net) + " ISK net" : "nothing recorded to value";

    /// <summary>The own share over these activities, or null where none of them was valued — the one sum
    /// <see cref="NetFor"/> and the summary's figures (ET-294) are both made of.</summary>
    public static decimal? Net<T>(IEnumerable<T> activities) where T : IRunsActivityFigures
    {
        decimal[] known = [.. activities.Where(activity => activity.NetIsk.HasValue).Select(activity => activity.NetIsk!.Value)];
        return known.Length == 0 ? null : known.Sum();
    }

    /// <summary>"+2.46B", "-3.1M": compact, with the plus a figure that was earned carries.</summary>
    public static string Signed(decimal value) => (value < 0 ? string.Empty : "+") + IskFormat.Compact(value);

    /// <summary>What each source brought in over the same activities, for the source bar beside a total (ET-290) — the
    /// very breakdowns <see cref="NetFor"/> adds up, split by source rather than summed a second way, so the own share
    /// (ET-296) is the one source both read.</summary>
    public static IskBreakdown SourcesFor<T>(IReadOnlyList<T> activities) where T : IRunsActivityFigures =>
        new([.. activities
            .SelectMany(activity => activity.Isk.Contributions)
            .Where(part => part.Certainty is not IskCertainty.Unknown)
            .GroupBy(part => part.Source)
            .Select(source => new IskContribution(source.Key, source.Sum(part => part.Amount), source.First().Certainty))]);
}
