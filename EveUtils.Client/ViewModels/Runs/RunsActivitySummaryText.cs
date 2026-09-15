using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>The "N activities" and net-ISK phrases a day band and a month total (ET-233) both say about a set of
/// rows — one formula so the two can never disagree over rows they both hold. Never a "0 ISK": a zero here would
/// read as a valuation that was taken and came out at nothing, rather than nothing having been valued at all
/// (ET-161 AC-4).</summary>
internal static class RunsActivitySummaryText
{
    public static string ActivitiesCount(int count) => $"{count} {(count == 1 ? "activity" : "activities")}";

    public static string NetFor(IReadOnlyList<ActivityOverviewRowViewModel> rows)
    {
        decimal[] known = [.. rows.Where(row => row.NetIsk.HasValue).Select(row => row.NetIsk!.Value)];
        return known.Length == 0
            ? "nothing recorded to value"
            : (known.Sum() < 0 ? string.Empty : "+") + IskFormat.Compact(known.Sum()) + " ISK net";
    }

    /// <summary>What each source brought in over the same rows, for the source bar beside a total (ET-290) — the very
    /// breakdowns <see cref="NetFor"/> adds up, split by source rather than summed a second way, so the eventual switch
    /// to the pilots' own share (ET-296) changes the one source both read.</summary>
    public static IskBreakdown SourcesFor(IReadOnlyList<ActivityOverviewRowViewModel> rows) =>
        new([.. rows
            .SelectMany(row => row.Isk.Contributions)
            .Where(part => part.Certainty is not IskCertainty.Unknown)
            .GroupBy(part => part.Source)
            .Select(source => new IskContribution(source.Key, source.Sum(part => part.Amount), source.First().Certainty))]);
}
