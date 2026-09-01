using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunEnemyObservation
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int EnemyTypeId { get; set; }
    public string EnemyName { get; set; } = string.Empty;
    public EnemyObservationDirection Direction { get; set; }
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime LastObservedAtUtc { get; set; }
    public Run? Run { get; set; }
}
