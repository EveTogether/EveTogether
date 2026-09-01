using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Activity;

public sealed partial class RunEnemyObservationViewModel(
    int enemyTypeId, string enemyName, DateTime observedAtUtc, EnemyObservationDirection direction) : ObservableObject
{
    public int EnemyTypeId { get; } = enemyTypeId;
    public string EnemyName { get; } = enemyName;

    /// <summary>Which way the damage went on the log lines this row was built from — read from the log, never
    /// assumed. A rat you shot and a rat that shot you are separate rows.</summary>
    public EnemyObservationDirection Direction { get; } = direction;

    /// <summary>When this enemy type was first and last seen in this direction. Stamped as observed rather than
    /// derived from the run's own start and stop, so the stored window is what was witnessed.</summary>
    public DateTime FirstObservedAtUtc { get; } = observedAtUtc;

    public DateTime LastObservedAtUtc { get; private set; } = observedAtUtc;

    /// <summary>Two rows for the same rat would otherwise read identically in the list.</summary>
    public string DirectionText => Direction is EnemyObservationDirection.To ? "you shot" : "shot you";

    [ObservableProperty]
    private int _count;

    internal void Observe(DateTime observedAtUtc)
    {
        if (observedAtUtc > LastObservedAtUtc)
            LastObservedAtUtc = observedAtUtc;
    }
}
