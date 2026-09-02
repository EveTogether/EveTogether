namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>
/// One enemy <em>type</em> met during a run, and how many of it the player counted. There is deliberately no
/// direction here: the question a run answers is which kind of enemy, and how many — a rat you shot and a rat that
/// shot you are the same kind, so keying on direction would put the same enemy in the list twice (ET-115).
/// </summary>
public sealed class RunEnemyObservationInput
{
    public int Count { get; init; }
    public required int EnemyTypeId { get; init; }
    public required string EnemyName { get; init; }
    public required DateTime FirstObservedAtUtc { get; init; }
    public required DateTime LastObservedAtUtc { get; init; }
}
