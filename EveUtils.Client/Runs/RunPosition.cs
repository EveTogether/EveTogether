using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Client.Runs;

/// <summary>
/// Where one saved run stands against its activity's earnings (ET-520).
/// </summary>
/// <param name="Run">The run's yield at today's price, whether the policy counts it, and its most valuable drop.</param>
/// <param name="Basis">The figures the run is held against: a server's shared ones when there are any, else the pilot's own.</param>
/// <param name="Answers">Every server that gave a figure, for the switch on the source chip.</param>
/// <param name="Reason">Why <paramref name="Basis"/> is the pilot's own runs and not a server's.</param>
/// <param name="Rank">Null where there are too few other runs to rank against, where the policy leaves the run out, or where
/// the figures have no typical run at all.</param>
public sealed record RunPosition(string ActivityRef, ActivityYieldDto Run, ExpectedEarnings Basis,
    IReadOnlyList<ExpectedEarnings> Answers, OwnRunsReason Reason, RunStanding? Rank)
{
    /// <summary>The run held against <paramref name="basis"/>: the one place a rank is made, so the first reading and a switch
    /// to another server's figures cannot disagree about how.</summary>
    public static RunPosition From(string activityRef, ActivityYieldDto run, ExpectedEarnings basis,
        IReadOnlyList<ExpectedEarnings> answers, OwnRunsReason reason)
    {
        EarningsFiguresDto figures = basis.Figures;
        // A run the policy leaves out is no part of the distribution, and ranking it would put a run the figures do not
        // know among the ones they do.
        RunStanding? rank = figures.Typical is null || !run.IsCounted
            ? null
            : RunRank.Of(figures.Yields, run.YieldIsk, activityRef, run.CompletedAtUtc >= figures.SinceUtc,
                EarningsPolicy.For(run.Key.Kind).MinimumRuns);
        return new RunPosition(activityRef, run, basis, answers, reason, rank);
    }
}
