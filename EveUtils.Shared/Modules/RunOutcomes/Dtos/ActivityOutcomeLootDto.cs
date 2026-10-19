using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Dtos;

/// <summary>The loot of one activity's shared outcomes in a window, under the same minimum of pilots as
/// <see cref="ActivityOutcomesDto"/>.</summary>
public sealed class ActivityOutcomeLootDto
{
    public required SharedOutcomesAvailability Availability { get; init; }
    public required int PilotCount { get; init; }
    public required IReadOnlyList<RunOutcomeLootDto> Outcomes { get; init; }
}
