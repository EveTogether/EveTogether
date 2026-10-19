using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.RunOutcomes;

/// <summary>
/// The per-pilot cap applied on the server (ET-514), so the contributor bucket never has to leave it: each outcome of
/// one activity in a window weighs min(1, C / that contributor's counted outcomes), by the same rule and policy the
/// client's <see cref="EarningsStatistics"/> weighs its own runs with.
/// </summary>
internal static class RunOutcomeWeights
{
    /// <param name="outcomes">The outcomes of one activity in one window.</param>
    public static IReadOnlyList<(RunOutcome Outcome, double Weight)> Of(IReadOnlyList<RunOutcome> outcomes)
    {
        if (outcomes.Count == 0)
            return [];

        EarningsPolicy policy = EarningsPolicy.For(outcomes[0].StatsKind);
        IReadOnlyList<double> weights = EarningsStatistics.CapWeights([.. outcomes.Select(_Sample)], policy);
        return [.. outcomes.Select((outcome, index) => (outcome, weights[index]))];
    }

    /// <summary>The contributors behind the outcomes the statistic counts.</summary>
    public static int PilotCount(IReadOnlyList<(RunOutcome Outcome, double Weight)> weighted) =>
        weighted.Where(entry => entry.Weight > 0).Select(entry => entry.Outcome.ContributorBucket).Distinct(StringComparer.Ordinal).Count();

    public static SharedOutcomesAvailability Availability(int pilotCount) =>
        pilotCount >= RunOutcomesModule.MinimumPilots ? SharedOutcomesAvailability.Shared : SharedOutcomesAvailability.TooFewPilots;

    // Only what the cap and the exclusions read: the window is already applied by the query.
    private static EarningsSample _Sample(RunOutcome outcome) => new()
    {
        PilotBucket = outcome.ContributorBucket,
        CompletedAtUtc = outcome.CompletedOn,
        DurationSeconds = outcome.DurationSeconds,
        FixedIsk = outcome.FixedIsk,
        LootBasis = outcome.LootBasis,
        FleetSize = outcome.FleetSize
    };
}
