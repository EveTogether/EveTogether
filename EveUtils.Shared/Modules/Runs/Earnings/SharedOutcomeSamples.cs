using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>The earnings samples of the outcomes a server handed out (ET-514, ET-516, ET-519): its outcomes carry the
/// fixed ISK and its loot comes apart, so the two lists are read side by side.</summary>
internal static class SharedOutcomeSamples
{
    /// <summary>Two reads of the same list a moment apart: a share landing between them shifts the rows, and figures built
    /// from a loot line that belongs to another outcome are worse than none.</summary>
    public static bool Match(IReadOnlyList<RunOutcomeDto> outcomes, IReadOnlyList<RunOutcomeLootDto> loot) =>
        outcomes.Count == loot.Count
        && outcomes.Zip(loot).All(pair => pair.First.CompletedOn == pair.Second.CompletedOn
                                          && pair.First.FleetSize == pair.Second.FleetSize
                                          && pair.First.LootBasis == pair.Second.LootBasis
                                          && pair.First.Weight == pair.Second.Weight);

    public static IEnumerable<EarningsSample> Of(IReadOnlyList<RunOutcomeDto> outcomes, IReadOnlyList<RunOutcomeLootDto> loot) =>
        outcomes.Zip(loot, _Sample);

    private static EarningsSample _Sample(RunOutcomeDto outcome, RunOutcomeLootDto loot) => new()
    {
        PilotBucket = string.Empty,
        CapWeight = outcome.Weight,
        CompletedAtUtc = outcome.CompletedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        DurationSeconds = outcome.DurationSeconds,
        FixedIsk = outcome.FixedIsk,
        LootGained = _Lines(loot, RunOutcomeLootKind.Gained),
        LootLost = _Lines(loot, RunOutcomeLootKind.Lost),
        Mined = _Lines(loot, RunOutcomeLootKind.Mined),
        LootBasis = outcome.LootBasis,
        OpenUnrecognisedLootCount = outcome.OpenUnrecognisedLootCount,
        FleetSize = outcome.FleetSize
    };

    private static EarningsLootLine[] _Lines(RunOutcomeLootDto loot, RunOutcomeLootKind kind) =>
        [.. loot.Lines.Where(line => line.Kind == kind).Select(line => new EarningsLootLine(line.TypeId, line.Quantity))];
}
