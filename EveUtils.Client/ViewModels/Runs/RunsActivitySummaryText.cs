using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Formatting;

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
}
