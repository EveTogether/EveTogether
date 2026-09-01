using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunEnemyObservationInput
{
    public int Count { get; init; } = 1;
    public required int EnemyTypeId { get; init; }
    public required string EnemyName { get; init; }
    public required EnemyObservationDirection Direction { get; init; }
    public required DateTime FirstObservedAtUtc { get; init; }
    public required DateTime LastObservedAtUtc { get; init; }
}
