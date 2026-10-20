using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>Where one run stands among the other runs of its activity (ET-520).</summary>
/// <param name="Percentile">0-1: the weighted share of the other runs this one beat, a tie counting half.</param>
/// <param name="OtherRunCount">The other runs the rank is held against.</param>
/// <param name="OtherEffectiveRuns">Their summed weights, the figure the minimum is held against.</param>
public sealed record RunStanding(double Percentile, int OtherRunCount, double OtherEffectiveRuns);

/// <summary>
/// The rank of a run in the distribution its activity's figures were made from (ET-520). A run that is part of that
/// distribution is taken out of it first, so it is never "better than itself": by its reference when the entry carries
/// one (the pilot's own runs), else by the entry of the same yield (a server's anonymous outcomes, which hold the run
/// once it was published). A run that is not in the distribution (the policy leaves it out, it is older than the window)
/// is ranked against all of it.
/// </summary>
public static class RunRank
{
    /// <param name="yields">The counted yields of the activity, as the figures hold them.</param>
    /// <param name="activityRef">The run's own reference, to find its entry exactly where entries carry one.</param>
    /// <param name="isInDistribution">The policy counts the run and it lies inside the window of the figures.</param>
    /// <param name="minimumOthers">Fewer effective runs than this besides the run itself and there is no rank.</param>
    public static RunStanding? Of(IReadOnlyList<EarningsYieldDto> yields, decimal runYield, string? activityRef,
        bool isInDistribution, double minimumOthers)
    {
        List<EarningsYieldDto> others = [.. yields];
        if (isInDistribution && _SelfEntry(others, runYield, activityRef) is { } own)
            others.Remove(own);

        double total = others.Sum(entry => entry.Weight);
        if (others.Count == 0 || total < minimumOthers)
            return null;

        double below = others.Where(entry => entry.YieldIsk < runYield).Sum(entry => entry.Weight);
        double tied = others.Where(entry => entry.YieldIsk == runYield).Sum(entry => entry.Weight);
        return new RunStanding((below + tied / 2) / total, others.Count, total);
    }

    private static EarningsYieldDto? _SelfEntry(IReadOnlyList<EarningsYieldDto> entries, decimal runYield, string? activityRef) =>
        activityRef is not null && entries.FirstOrDefault(entry => entry.ActivityRef == activityRef) is { } byReference
            ? byReference
            : entries.FirstOrDefault(entry => entry.ActivityRef is null && entry.YieldIsk == runYield);
}
