using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Dtos;

/// <summary>The loot lines of one shared outcome, for the loot table (ET-519), with the same weight as its
/// <see cref="RunOutcomeDto"/> and no more about who or where.</summary>
public sealed class RunOutcomeLootDto
{
    public required DateOnly CompletedOn { get; init; }
    public required int FleetSize { get; init; }
    public required LootBasis LootBasis { get; init; }
    public required double Weight { get; init; }
    public required IReadOnlyList<RunOutcomeLootLineDto> Lines { get; init; }
}
