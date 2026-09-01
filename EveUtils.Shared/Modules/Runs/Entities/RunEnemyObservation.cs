namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunEnemyObservation
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int EnemyTypeId { get; set; }
    public string EnemyName { get; set; } = string.Empty;

    /// <summary>How many of this type the player counted. Only a run row with a count above zero is stored at all,
    /// so this column is never the default (ET-106).</summary>
    public int Count { get; set; }

    /// <summary>First and last sighting across every observation of this type, both ways round.</summary>
    public DateTime FirstObservedAtUtc { get; set; }

    public DateTime LastObservedAtUtc { get; set; }
    public Run? Run { get; set; }
}
