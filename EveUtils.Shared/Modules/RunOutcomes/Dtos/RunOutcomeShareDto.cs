using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Dtos;

/// <summary>What a pilot shares of one activity (ET-514): an <see cref="EarningsSample"/> without its pilot, keyed by
/// <see cref="ActivityStatsKey"/>. Who shared it and which activity it was are the server's to pseudonymise.</summary>
public sealed class RunOutcomeShareDto
{
    public required ActivityStatsKey Key { get; init; }
    public required DateOnly CompletedOn { get; init; }
    public int? DurationSeconds { get; init; }
    public required decimal FixedIsk { get; init; }
    public int FleetSize { get; init; } = 1;
    public LootBasis LootBasis { get; init; }
    public int OpenUnrecognisedLootCount { get; init; }
    public IReadOnlyList<RunOutcomeLootLineDto> LootLines { get; init; } = [];
}
