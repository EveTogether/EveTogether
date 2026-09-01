using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

public sealed partial class RunEnemyObservationViewModel(int enemyTypeId, string enemyName, DateTime observedAtUtc)
    : ObservableObject
{
    public int EnemyTypeId { get; } = enemyTypeId;
    public string EnemyName { get; } = enemyName;

    /// <summary>When this enemy type was first and last seen. Stamped as observed rather than derived from the run's
    /// own start and stop, so the stored window is what was witnessed and not the run's outline.</summary>
    public DateTime FirstObservedAtUtc { get; } = observedAtUtc;

    public DateTime LastObservedAtUtc { get; private set; } = observedAtUtc;

    [ObservableProperty]
    private int _count;

    internal void Observe(DateTime observedAtUtc)
    {
        if (observedAtUtc > LastObservedAtUtc)
            LastObservedAtUtc = observedAtUtc;
    }
}
