using System;
using System.Collections.Generic;
using EveUtils.Client.Formatting;
using EveUtils.Client.Runs;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>The "N activities", flown time and net-ISK phrases a day header, a strip cell and the range line (ET-233,
/// ET-292) all say about a set of activities — worded over <see cref="RunTotals"/>, so none of them can disagree over
/// activities they share. Never a "0 ISK": a zero here would read as a valuation that was taken and came out at
/// nothing, rather than nothing having been valued at all (ET-161 AC-4).</summary>
internal static class RunsActivitySummaryText
{
    public static string ActivitiesCount(int count) => $"{count} {(count == 1 ? "activity" : "activities")}";

    public static string FlownFor<T>(IReadOnlyList<T> activities) where T : IRunsActivityFigures =>
        Flown(activities) + " flown";

    /// <summary>"24:18:33" — hours past a day keep counting rather than wrapping.</summary>
    public static string Flown<T>(IReadOnlyCollection<T> activities) where T : IRunsActivityFigures
    {
        TimeSpan flown = RunTotals.Flown(activities);
        return $"{(int)flown.TotalHours}:{flown.Minutes:00}:{flown.Seconds:00}";
    }

    public static string NetFor<T>(IReadOnlyList<T> activities) where T : IRunsActivityFigures =>
        RunTotals.Net(activities) is { } net ? Signed(net) + " ISK net" : "nothing recorded to value";

    /// <summary>"+2.46B", "-3.1M": compact, with the plus a figure that was earned carries.</summary>
    public static string Signed(decimal value) => (value < 0 ? string.Empty : "+") + IskFormat.Compact(value);
}
